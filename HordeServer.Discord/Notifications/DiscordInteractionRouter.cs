// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Text.Json;
using HordeServer.Discord.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HordeServer.Discord.Notifications
{
	/// <summary>
	/// What a button press turns into.
	/// </summary>
	/// <param name="Interaction">The raw interaction, for anything the parsed fields do not cover.</param>
	/// <param name="CustomId">The parsed identity of the component that was used.</param>
	/// <param name="DiscordUserId">Snowflake of whoever pressed it.</param>
	public sealed record DiscordInteractionContext(DiscordInteraction Interaction, DiscordCustomId CustomId, string DiscordUserId);

	/// <summary>
	/// Turns gateway interactions into calls on whoever registered for them.
	/// </summary>
	/// <remarks>
	/// The whole reason this type exists is Discord's **three-second deadline**. An interaction that is not answered
	/// within three seconds is shown to the person who clicked as a failure, and the token is then useless - so the
	/// answer cannot wait for whatever the button actually does. Horde's issue operations are database work behind a
	/// service call, and three seconds is not a budget worth betting an operator's triage flow on.
	///
	/// So the order is fixed: acknowledge first, act second. The acknowledgement is a *deferred update*, which stops
	/// the button spinning and changes nothing else, leaving fifteen minutes to edit the message once the work is
	/// done. Handlers therefore never see the deadline and cannot accidentally blow it.
	///
	/// It is also the boundary where a badly behaved handler stops being the gateway's problem. Handlers run on a
	/// task of their own rather than on the receive loop, because the receive loop is also what reads heartbeat
	/// acknowledgements - a handler that blocked it would eventually be diagnosed as a dead connection and provoke a
	/// reconnect.
	/// </remarks>
	public sealed class DiscordInteractionRouter : IHostedService
	{
		/// <summary>
		/// Gateway event carrying an interaction.
		/// </summary>
		public const string InteractionCreate = "INTERACTION_CREATE";

		readonly DiscordGateway _gateway;
		readonly DiscordClient _client;
		readonly IOptions<DiscordServerConfig> _serverConfig;
		readonly ILogger _logger;

		readonly ConcurrentDictionary<string, Registration> _handlers
			= new ConcurrentDictionary<string, Registration>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="gateway">Gateway to listen to.</param>
		/// <param name="client">Client to answer interactions with.</param>
		/// <param name="serverConfig">Server configuration, for the application id.</param>
		/// <param name="logger">Logger for interaction handling.</param>
		public DiscordInteractionRouter(DiscordGateway gateway, DiscordClient client, IOptions<DiscordServerConfig> serverConfig, ILogger<DiscordInteractionRouter> logger)
		{
			_gateway = gateway;
			_client = client;
			_serverConfig = serverConfig;
			_logger = logger;
		}

		/// <inheritdoc/>
		public Task StartAsync(CancellationToken cancellationToken)
		{
			_gateway.DispatchReceived += OnDispatch;

			_logger.LogInformation("Discord interaction router listening ({Scopes})",
				_handlers.IsEmpty ? "no scopes registered yet" : String.Join(", ", _handlers.Keys));

			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public Task StopAsync(CancellationToken cancellationToken)
		{
			_gateway.DispatchReceived -= OnDispatch;
			return Task.CompletedTask;
		}

		/// <summary>
		/// Registers what to do when a button with the given scope is pressed.
		/// </summary>
		/// <remarks>
		/// By scope rather than by verb, because the verbs of one scope belong together - everything
		/// <c>issue</c> does is one handler's business - and because it keeps the set of registrations small enough
		/// to name in a log line when nothing matches.
		/// </remarks>
		/// <param name="scope">First segment of the custom id, such as <see cref="DiscordCustomId.IssueScope"/>.</param>
		/// <param name="handler">What to do. Runs after the interaction has already been acknowledged, unless
		/// <paramref name="answersForItself"/> says otherwise.</param>
		/// <param name="answersForItself">
		/// Verbs whose handler produces the first response itself, rather than being pre-acknowledged.
		/// </param>
		public void Register(
			string scope,
			Func<DiscordInteractionContext, CancellationToken, Task> handler,
			Func<DiscordCustomId, bool>? answersForItself = null)
			=> _handlers[scope] = new Registration(handler, answersForItself);

		/// <summary>
		/// Handles one interaction, acknowledging it before doing anything that could be slow.
		/// </summary>
		/// <param name="interaction">The interaction as it arrived.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public async Task HandleAsync(DiscordInteraction interaction, CancellationToken cancellationToken)
		{
			if (interaction.Id == null || interaction.Token == null)
			{
				_logger.LogWarning("Discord sent an interaction with no id or token, which cannot be answered");
				return;
			}

			if (interaction.Type is not (DiscordInteractionType.MessageComponent or DiscordInteractionType.ModalSubmit))
			{
				// Slash commands arrive here too, and will be handled when they exist. Until then, saying nothing is
				// better than acknowledging something we will not act on.
				_logger.LogDebug("Ignoring interaction of type {Type}", interaction.Type);
				return;
			}

			if (!DiscordCustomId.TryParse(interaction.CustomId, out DiscordCustomId? customId))
			{
				_logger.LogWarning("Discord component '{CustomId}' is not one of ours", interaction.CustomId ?? "<none>");
				return;
			}

			if (!_handlers.TryGetValue(customId!.Scope, out Registration? registration))
			{
				_logger.LogWarning("Nothing is registered for interaction scope '{Scope}'. Registered: {Registered}",
					customId.Scope, _handlers.IsEmpty ? "<none>" : String.Join(", ", _handlers.Keys));
				return;
			}

			string? userId = interaction.UserId;

			if (userId == null)
			{
				_logger.LogWarning("Discord interaction {InteractionId} identified nobody as its user", interaction.Id);
				return;
			}

			// Opening a modal has to *be* the first response - Discord refuses to attach a dialog to an interaction
			// that has already been answered, deferral included. Such a handler therefore runs unacknowledged and
			// owes Discord an answer inside the three seconds itself, which is affordable because opening a modal is
			// one request and nothing else.
			bool answersForItself = registration!.AnswersForItself?.Invoke(customId) ?? false;

			if (!answersForItself)
			{
				// Before the work, always. Everything below this line has fifteen minutes; everything above it had
				// three seconds.
				if (!await _client.RespondToInteractionAsync(interaction.Id, interaction.Token, DiscordInteractionResponse.Acknowledge(), cancellationToken))
				{
					// The acknowledgement is what makes the token usable. Running the handler anyway would do the
					// work and then have no way to report it, which is worse than not doing it - the operator sees a
					// failed button and will press it again.
					_logger.LogError("Could not acknowledge interaction {InteractionId}; '{CustomId}' was not run",
						interaction.Id, customId);
					return;
				}
			}

			try
			{
				await registration.Handler(new DiscordInteractionContext(interaction, customId, userId), cancellationToken);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Handling Discord interaction '{CustomId}' from user {UserId} failed", customId, userId);
			}
		}

		/// <summary>
		/// Replaces the message an interaction came from.
		/// </summary>
		/// <remarks>
		/// Goes through the interaction token rather than through the channel, so it works for a message the bot
		/// would otherwise have no permission to edit.
		/// </remarks>
		/// <param name="context">Interaction being responded to.</param>
		/// <param name="message">Replacement content.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>True if the edit was accepted.</returns>
		public async Task<bool> UpdateMessageAsync(DiscordInteractionContext context, DiscordMessage message, CancellationToken cancellationToken)
		{
			string? applicationId = ApplicationIdFor(context);

			if (applicationId == null || context.Interaction.Token == null)
			{
				return false;
			}

			return await _client.EditInteractionResponseAsync(applicationId, context.Interaction.Token, message, cancellationToken);
		}

		/// <summary>
		/// Answers an interaction directly, for a handler registered as answering for itself.
		/// </summary>
		/// <remarks>
		/// The only way to open a modal, and subject to the three-second deadline - so a handler using this must do
		/// nothing slow first. Use <see cref="DiscordInteractionResponse.OpenModal"/> or
		/// <see cref="DiscordInteractionResponse.Ephemeral"/> to build the response.
		/// </remarks>
		/// <param name="context">Interaction being answered.</param>
		/// <param name="response">The answer.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>True if Discord accepted it.</returns>
		public async Task<bool> RespondAsync(DiscordInteractionContext context, DiscordInteractionResponse response, CancellationToken cancellationToken)
		{
			if (context.Interaction.Id == null || context.Interaction.Token == null)
			{
				return false;
			}

			return await _client.RespondToInteractionAsync(
				context.Interaction.Id, context.Interaction.Token, response, cancellationToken);
		}

		/// <summary>
		/// Posts a further message against an interaction already answered.
		/// </summary>
		/// <param name="context">Interaction to hang it off.</param>
		/// <param name="message">Message to post.</param>
		/// <param name="ephemeral">Whether only the person who acted should see it.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>True if it was accepted.</returns>
		public async Task<bool> FollowUpAsync(DiscordInteractionContext context, DiscordMessage message, bool ephemeral, CancellationToken cancellationToken)
		{
			string? applicationId = ApplicationIdFor(context);

			if (applicationId == null || context.Interaction.Token == null)
			{
				return false;
			}

			if (ephemeral)
			{
				message.Flags = DiscordMessageFlags.Ephemeral;
			}

			return await _client.CreateFollowupMessageAsync(applicationId, context.Interaction.Token, message, cancellationToken);
		}

		string? ApplicationIdFor(DiscordInteractionContext context)
		{
			// Configured value first, but the interaction carries one too - which is what lets the smoke tool work
			// with no ApplicationId set at all.
			string? applicationId = _serverConfig.Value.ApplicationId ?? context.Interaction.ApplicationId;

			if (applicationId == null)
			{
				_logger.LogError("Cannot address interaction {InteractionId}: no application id is configured and "
					+ "the interaction did not carry one.", context.Interaction.Id);
			}

			return applicationId;
		}

		/// <summary>
		/// A registered handler and the circumstances under which it is left to answer for itself.
		/// </summary>
		/// <param name="Handler">What to run.</param>
		/// <param name="AnswersForItself">
		/// Decides, per custom id, whether the pre-acknowledgement is skipped. Null means never.
		/// </param>
		sealed record Registration(
			Func<DiscordInteractionContext, CancellationToken, Task> Handler,
			Func<DiscordCustomId, bool>? AnswersForItself);

		void OnDispatch(DiscordGatewayDispatch dispatch)
		{
			if (dispatch.EventName != InteractionCreate)
			{
				return;
			}

			DiscordInteraction? interaction;

			try
			{
				interaction = dispatch.Data.Deserialize<DiscordInteraction>();
			}
			catch (JsonException ex)
			{
				_logger.LogError(ex, "Could not read an interaction Discord sent");
				return;
			}

			if (interaction == null)
			{
				return;
			}

			// Off the receive loop deliberately - see the remarks on this class. Nothing awaits this task, so it
			// must not be able to throw, which is why HandleAsync catches everything a handler does.
			_ = Task.Run(() => HandleAsync(interaction, CancellationToken.None));
		}
	}
}
