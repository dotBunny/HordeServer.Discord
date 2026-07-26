// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HordeServer.Discord.Client
{
	/// <summary>
	/// Minimal REST client for the Discord API.
	/// </summary>
	/// <remarks>
	/// Hand-rolled rather than taking <c>Discord.Net</c> or <c>DSharpPlus</c>, mirroring how Epic hand-rolls
	/// <c>EpicGames.Slack</c>. The plugin is dropped into someone else's server process, where a package dependency
	/// is not just a package dependency: assembly resolution is first-load-wins, so a version collision between our
	/// transitive graph and the server's own is a genuinely nasty runtime bug to diagnose. See
	/// <c>.claude/PLAN.md</c> section 3.2. This way the drop stays one file.
	///
	/// Every call goes through <see cref="DiscordRateLimiter"/>, and every failure is logged and reported as a null
	/// or false rather than thrown. The notification service already isolates sinks from each other, but an
	/// exception escaping here would still mean a notification that vanished without saying why.
	/// </remarks>
	public sealed class DiscordClient : IDisposable
	{
		/// <summary>
		/// Base address for the API, with the version pinned.
		/// </summary>
		/// <remarks>
		/// The version in the path is a correctness requirement, not hygiene: an unversioned request does not get
		/// the current API, it silently routes to v6, which is deprecated. Note the docs moved to
		/// <c>docs.discord.com</c> but the API itself did not.
		/// </remarks>
		public const string ApiBaseUrl = "https://discord.com/api/v10/";

		/// <summary>
		/// Maximum characters in a thread name.
		/// </summary>
		public const int ThreadNameLength = 100;

		/// <summary>
		/// How long a triage thread stays out of the archive with no activity, in minutes.
		/// </summary>
		/// <remarks>
		/// A week, the longest Discord allows. Triage threads are read by whoever picks the issue up, which may be
		/// after a weekend; an archived thread is still readable but drops out of the channel list, which is exactly
		/// where somebody would look for it. Only 60, 1440, 4320 and 10080 are accepted.
		/// </remarks>
		public const int ThreadAutoArchiveMinutes = 10080;

		static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
		{
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		};

		readonly HttpClient _httpClient;
		readonly DiscordRateLimiter _rateLimiter;
		readonly ILogger _logger;

		// Discord user snowflake to the DM channel opened with them. Opening is idempotent and the id never changes,
		// so this is a pure saving: one request per person for the lifetime of the process instead of one per
		// notification.
		readonly ConcurrentDictionary<string, string> _directMessageChannels = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

		HttpClient? _ownedHttpClient;

		/// <summary>
		/// Creates a client that owns its own transport.
		/// </summary>
		/// <remarks>
		/// Deliberately not <c>IHttpClientFactory</c>. A typed client is registered transient, so holding one in the
		/// singleton sink would be a captive dependency and would defeat the handler rotation that is the factory's
		/// whole point - and registering a bare <c>HttpClient</c> in the container would hijack the *host server's*
		/// <c>HttpClient</c> resolution, which is not a plugin's business. A pooled connection lifetime gets the one
		/// thing that actually matters here, which is not pinning a stale DNS answer for the life of the process.
		/// </remarks>
		/// <param name="serverConfig">Server configuration, for the bot token.</param>
		/// <param name="rateLimiter">Limiter every request is routed through.</param>
		/// <param name="logger">Logger for API failures.</param>
		/// <returns>A client that disposes its transport when disposed.</returns>
		public static DiscordClient Create(IOptions<DiscordServerConfig> serverConfig, DiscordRateLimiter rateLimiter, ILogger<DiscordClient> logger)
		{
			HttpClient httpClient = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15.0) });

			return new DiscordClient(httpClient, serverConfig, rateLimiter, logger) { _ownedHttpClient = httpClient };
		}

		/// <inheritdoc/>
		public void Dispose()
		{
			_ownedHttpClient?.Dispose();
			_ownedHttpClient = null;
		}

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="httpClient">Transport. Its base address and auth header are configured here if unset.</param>
		/// <param name="serverConfig">Server configuration, for the bot token.</param>
		/// <param name="rateLimiter">Limiter every request is routed through.</param>
		/// <param name="logger">Logger for API failures.</param>
		public DiscordClient(HttpClient httpClient, IOptions<DiscordServerConfig> serverConfig, DiscordRateLimiter rateLimiter, ILogger<DiscordClient> logger)
		{
			_httpClient = httpClient;
			_rateLimiter = rateLimiter;
			_logger = logger;

			_httpClient.BaseAddress ??= new Uri(ApiBaseUrl);

			if (_httpClient.DefaultRequestHeaders.Authorization == null && !String.IsNullOrEmpty(serverConfig.Value.BotToken))
			{
				// "Bot" prefix included: Discord treats a bare token as a (long dead) user token and rejects it.
				_httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", serverConfig.Value.BotToken);
			}

			if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
			{
				// Discord requires this exact shape and rate limits unidentified clients more aggressively.
				_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
					$"DiscordBot (https://github.com/dotBunny/HordeServer.Discord, {GetPluginVersion()})");
			}
		}

		/// <summary>
		/// Posts a message to a channel.
		/// </summary>
		/// <param name="channelId">Channel snowflake to post to.</param>
		/// <param name="message">Message to send.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>A reference to the posted message, or null if it could not be sent.</returns>
		public async Task<DiscordMessageReference?> CreateMessageAsync(string channelId, DiscordMessage message, CancellationToken cancellationToken)
		{
			string payload = JsonSerializer.Serialize(message, s_jsonOptions);
			string path = $"channels/{channelId}/messages";

			using HttpResponseMessage response = await _rateLimiter.SendAsync(
				DiscordRoute.CreateMessage(channelId),
				token => _httpClient.SendAsync(CreateRequest(HttpMethod.Post, path, payload), token),
				cancellationToken);

			if (!await IsSuccessAsync(response, "post a message to channel {ChannelId}", channelId))
			{
				return null;
			}

			return await ReadMessageReferenceAsync(response, channelId, cancellationToken);
		}

		/// <summary>
		/// Opens the direct message channel with a user, or returns the one already open.
		/// </summary>
		/// <remarks>
		/// Discord has no "send a DM to this user" endpoint. A DM is an ordinary channel that happens to have two
		/// members, so it has to be opened first and then posted to like any other. Opening is idempotent and returns
		/// the same channel every time, which is what makes caching safe.
		///
		/// Returns null when the bot may not DM this person: it shares no guild with them, or they have direct
		/// messages from server members turned off. That is a normal, permanent state for some users rather than an
		/// error, so callers are expected to fall back to naming them in a channel. The failure is not cached - the
		/// setting is theirs to change, and re-checking costs one request against a route nothing else uses.
		/// </remarks>
		/// <param name="userId">Discord user snowflake to open a channel with.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>The DM channel snowflake, or null if one could not be opened.</returns>
		public async Task<string?> GetDirectMessageChannelAsync(string userId, CancellationToken cancellationToken)
		{
			if (_directMessageChannels.TryGetValue(userId, out string? cached))
			{
				return cached;
			}

			string payload = JsonSerializer.Serialize(new CreateDirectMessageChannel { RecipientId = userId }, s_jsonOptions);

			using HttpResponseMessage response = await _rateLimiter.SendAsync(
				DiscordRoute.CreateDirectMessageChannel(),
				token => _httpClient.SendAsync(CreateRequest(HttpMethod.Post, "users/@me/channels", payload), token),
				cancellationToken);

			if (!await IsSuccessAsync(response, "open a direct message channel with user {UserId}", userId))
			{
				return null;
			}

			try
			{
				CreatedMessage? channel = await response.Content.ReadFromJsonAsync<CreatedMessage>(s_jsonOptions, cancellationToken);

				if (channel?.Id == null)
				{
					_logger.LogError("Discord opened a direct message channel with user {UserId} but returned no channel id", userId);
					return null;
				}

				// Bounded by the size of the configured user map, which is hand-maintained, so this cannot grow
				// beyond the number of people someone has actually written down.
				_directMessageChannels[userId] = channel.Id;
				return channel.Id;
			}
			catch (JsonException ex)
			{
				_logger.LogError(ex, "Could not read the channel id Discord returned for user {UserId}", userId);
				return null;
			}
		}

		/// <summary>
		/// Replaces the content of a message already posted.
		/// </summary>
		/// <remarks>
		/// The edit-in-place half of the message-state design: an issue that changes gets its existing message
		/// rewritten rather than a new one posted beneath it.
		/// </remarks>
		/// <param name="reference">Message to edit.</param>
		/// <param name="message">Replacement content.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>True if the edit was accepted.</returns>
		public async Task<bool> EditMessageAsync(DiscordMessageReference reference, DiscordMessage message, CancellationToken cancellationToken)
		{
			string payload = JsonSerializer.Serialize(message, s_jsonOptions);
			string path = $"channels/{reference.ChannelId}/messages/{reference.MessageId}";

			using HttpResponseMessage response = await _rateLimiter.SendAsync(
				DiscordRoute.EditMessage(reference.ChannelId),
				token => _httpClient.SendAsync(CreateRequest(HttpMethod.Patch, path, payload), token),
				cancellationToken);

			return await IsSuccessAsync(response, "edit message {MessageId} in channel {ChannelId}", reference.MessageId, reference.ChannelId);
		}

		/// <summary>
		/// Starts a thread hanging off a message.
		/// </summary>
		/// <remarks>
		/// The thread Discord creates **takes the id of the message it was started from**, so the returned id is the
		/// message id and posting into the thread is posting to a channel with that id. Nothing else needs storing.
		///
		/// Starting a thread on a message that already has one returns <c>160004</c>, which is not really an error
		/// here - it means the thread exists, which is what the caller wanted - so it is reported as success.
		/// </remarks>
		/// <param name="channelId">Channel the message is in.</param>
		/// <param name="messageId">Message to start the thread from.</param>
		/// <param name="name">Thread name, truncated to Discord's limit.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>True if the thread exists afterwards.</returns>
		public async Task<bool> CreateThreadFromMessageAsync(string channelId, string messageId, string name, CancellationToken cancellationToken)
		{
			string payload = JsonSerializer.Serialize(
				new CreateThread
				{
					Name = DiscordEmbedLimits.Truncate(name, ThreadNameLength),
					AutoArchiveDuration = ThreadAutoArchiveMinutes,
				},
				s_jsonOptions);

			string path = $"channels/{channelId}/messages/{messageId}/threads";

			using HttpResponseMessage response = await _rateLimiter.SendAsync(
				DiscordRoute.CreateThreadFromMessage(channelId),
				token => _httpClient.SendAsync(CreateRequest(HttpMethod.Post, path, payload), token),
				cancellationToken);

			if (response.IsSuccessStatusCode)
			{
				return true;
			}

			string body = await ReadBodyForLoggingAsync(response);

			if (body.Contains("160004", StringComparison.Ordinal))
			{
				// "A thread has already been created for this message". Reaching here means two updates raced, which
				// is normal on a busy issue, and the thread the caller wanted is there either way.
				return true;
			}

			_logger.LogError("Discord API failed to start a thread on message {MessageId} in channel {ChannelId}: "
				+ "{StatusCode} {Body}", messageId, channelId, (int)response.StatusCode, body);

			return false;
		}

		/// <summary>
		/// Answers an interaction.
		/// </summary>
		/// <remarks>
		/// **Within three seconds of the interaction arriving**, or Discord shows the person who clicked a failure
		/// and refuses the response. That deadline is on this call only; once it has been met with a deferred
		/// acknowledgement, <see cref="EditInteractionResponseAsync"/> has fifteen minutes.
		///
		/// The interaction token in the path is what authorises this, not the bot token - which is why the same call
		/// works from a web service that holds no bot credentials at all. The bot header goes along anyway, because
		/// it is on the shared <see cref="HttpClient"/> and Discord ignores it here.
		/// </remarks>
		/// <param name="interactionId">Interaction being answered.</param>
		/// <param name="interactionToken">Continuation token it arrived with.</param>
		/// <param name="response">The answer.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>True if Discord accepted it.</returns>
		public async Task<bool> RespondToInteractionAsync(string interactionId, string interactionToken, DiscordInteractionResponse response, CancellationToken cancellationToken)
		{
			string payload = JsonSerializer.Serialize(response, s_jsonOptions);
			string path = $"interactions/{interactionId}/{interactionToken}/callback";

			using HttpResponseMessage result = await _rateLimiter.SendAsync(
				DiscordRoute.InteractionCallback(interactionId),
				token => _httpClient.SendAsync(CreateRequest(HttpMethod.Post, path, payload), token),
				cancellationToken);

			return await IsSuccessAsync(result, "respond to interaction {InteractionId}", interactionId);
		}

		/// <summary>
		/// Replaces the message an interaction was acknowledged against.
		/// </summary>
		/// <remarks>
		/// The other half of the deferred pattern. Valid for fifteen minutes from the interaction, and addressed by
		/// its token rather than by the channel, which is why it works even for an ephemeral reply that has no
		/// message id.
		/// </remarks>
		/// <param name="applicationId">Application the interaction belongs to.</param>
		/// <param name="interactionToken">Continuation token the interaction arrived with.</param>
		/// <param name="message">Replacement content.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>True if the edit was accepted.</returns>
		public async Task<bool> EditInteractionResponseAsync(string applicationId, string interactionToken, DiscordMessage message, CancellationToken cancellationToken)
		{
			string payload = JsonSerializer.Serialize(message, s_jsonOptions);
			string path = $"webhooks/{applicationId}/{interactionToken}/messages/@original";

			using HttpResponseMessage response = await _rateLimiter.SendAsync(
				DiscordRoute.InteractionResponse(),
				token => _httpClient.SendAsync(CreateRequest(HttpMethod.Patch, path, payload), token),
				cancellationToken);

			return await IsSuccessAsync(response, "edit the response to an interaction");
		}

		/// <summary>
		/// Posts an additional message against an interaction that has already been answered.
		/// </summary>
		/// <remarks>
		/// The half of the hybrid Mark Fixed flow that the deferral makes necessary. Once a modal submission has been
		/// acknowledged, the root-cause category question cannot be the *response* to it any more - so it is posted
		/// as a followup instead, ephemeral, after the fix has been applied. Fifteen minutes from the interaction,
		/// like every other use of the token.
		/// </remarks>
		/// <param name="applicationId">Application the interaction belongs to.</param>
		/// <param name="interactionToken">Continuation token the interaction arrived with.</param>
		/// <param name="message">Message to post. Set <see cref="DiscordMessageFlags.Ephemeral"/> to show it only to
		/// the person who acted.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>True if it was accepted.</returns>
		public async Task<bool> CreateFollowupMessageAsync(string applicationId, string interactionToken, DiscordMessage message, CancellationToken cancellationToken)
		{
			string payload = JsonSerializer.Serialize(message, s_jsonOptions);
			string path = $"webhooks/{applicationId}/{interactionToken}";

			using HttpResponseMessage response = await _rateLimiter.SendAsync(
				DiscordRoute.InteractionFollowup(),
				token => _httpClient.SendAsync(CreateRequest(HttpMethod.Post, path, payload), token),
				cancellationToken);

			return await IsSuccessAsync(response, "post a followup to an interaction");
		}

		/// <summary>
		/// Asks Discord which gateway host to connect to.
		/// </summary>
		/// <remarks>
		/// Not a constant, and not cached across sessions. Discord hands out a URL per bot and reserves the right to
		/// move it; the documented contract is to ask again for each new connection rather than to remember one. A
		/// *resume* is the exception and goes to the <c>resume_gateway_url</c> from <c>READY</c> instead, which is
		/// why <see cref="DiscordGateway"/> only calls this when starting a fresh session.
		///
		/// The <c>/gateway/bot</c> form rather than plain <c>/gateway</c> because it authenticates, so a bad token
		/// surfaces here as a clean 401 rather than as a websocket that connects and is then closed with 4004.
		/// </remarks>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>The websocket URL, or null if it could not be retrieved.</returns>
		public async Task<string?> GetGatewayUrlAsync(CancellationToken cancellationToken)
		{
			using HttpResponseMessage response = await _rateLimiter.SendAsync(
				DiscordRoute.GetGatewayBot(),
				token => _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Get, "gateway/bot"), token),
				cancellationToken);

			if (!await IsSuccessAsync(response, "look up the gateway URL"))
			{
				return null;
			}

			try
			{
				GatewayInfo? info = await response.Content.ReadFromJsonAsync<GatewayInfo>(s_jsonOptions, cancellationToken);

				if (info?.Url == null)
				{
					_logger.LogError("Discord returned no gateway URL");
					return null;
				}

				// Worth logging once per session. A bot that has exhausted its daily identify allowance connects,
				// gets closed, and reconnects forever, and this is the only place that says so.
				if (info.SessionStartLimit != null && info.SessionStartLimit.Remaining <= 0)
				{
					_logger.LogError("Discord reports no session starts remaining (resets in {ResetAfter}ms). "
						+ "Repeated reconnects will fail until then.", info.SessionStartLimit.ResetAfter);
				}

				return info.Url;
			}
			catch (JsonException ex)
			{
				_logger.LogError(ex, "Could not read the gateway URL Discord returned");
				return null;
			}
		}

		static HttpRequestMessage CreateRequest(HttpMethod method, string path, string payload)
			=> new HttpRequestMessage(method, path)
			{
				Content = new StringContent(payload, Encoding.UTF8, "application/json"),
			};


		async Task<bool> IsSuccessAsync(HttpResponseMessage response, string what, params object?[] args)
		{
			if (response.IsSuccessStatusCode)
			{
				return true;
			}

			// Discord's error bodies are the useful part - "Missing Permissions" with a code, rather than a bare
			// 403 that leaves you guessing between a bad channel id and a bot that was never invited.
			string body = await ReadBodyForLoggingAsync(response);

			_logger.LogError("Discord API failed to " + what + ": {StatusCode} {Body}",
				[.. args, (int)response.StatusCode, body]);

			return false;
		}

		async Task<DiscordMessageReference?> ReadMessageReferenceAsync(HttpResponseMessage response, string channelId, CancellationToken cancellationToken)
		{
			try
			{
				CreatedMessage? created = await response.Content.ReadFromJsonAsync<CreatedMessage>(s_jsonOptions, cancellationToken);

				if (created?.Id == null)
				{
					_logger.LogError("Discord accepted a message for channel {ChannelId} but returned no message id", channelId);
					return null;
				}

				return new DiscordMessageReference(created.ChannelId ?? channelId, created.Id);
			}
			catch (JsonException ex)
			{
				// Worth reporting rather than swallowing: the message did post, we just cannot edit it later.
				_logger.LogError(ex, "Could not read the message id Discord returned for channel {ChannelId}", channelId);
				return null;
			}
		}

		static async Task<string> ReadBodyForLoggingAsync(HttpResponseMessage response)
		{
			try
			{
				string body = await response.Content.ReadAsStringAsync();
				return DiscordEmbedLimits.Truncate(body, 512);
			}
			catch (Exception)
			{
				// The status code is the part that matters; failing to read the body must not replace a useful
				// error log with an unrelated exception.
				return "<no body>";
			}
		}

		static string GetPluginVersion()
			=> typeof(DiscordClient).Assembly
				.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
				?? "0.0.0";

		/// <summary>
		/// The parts of Discord's message object the client needs back.
		/// </summary>
		/// <remarks>
		/// Also serves the channel object returned when a DM is opened, which carries its id under the same property.
		/// </remarks>
		sealed class CreatedMessage
		{
			[JsonPropertyName("id")]
			public string? Id { get; set; }

			[JsonPropertyName("channel_id")]
			public string? ChannelId { get; set; }
		}

		/// <summary>
		/// Request body for opening a direct message channel.
		/// </summary>
		sealed class CreateDirectMessageChannel
		{
			[JsonPropertyName("recipient_id")]
			public string? RecipientId { get; set; }
		}

		/// <summary>
		/// Request body for starting a thread from a message.
		/// </summary>
		sealed class CreateThread
		{
			[JsonPropertyName("name")]
			public string? Name { get; set; }

			[JsonPropertyName("auto_archive_duration")]
			public int AutoArchiveDuration { get; set; }
		}

		/// <summary>
		/// Response body of <c>GET /gateway/bot</c>.
		/// </summary>
		sealed class GatewayInfo
		{
			[JsonPropertyName("url")]
			public string? Url { get; set; }

			[JsonPropertyName("session_start_limit")]
			public SessionStartLimit? SessionStartLimit { get; set; }
		}

		/// <summary>
		/// How many new gateway sessions the bot may still start today.
		/// </summary>
		/// <remarks>
		/// Separate from the request rate limit and much less forgiving: 1000 a day for a small bot. Resuming does
		/// not count against it, which is the practical reason the resume path in <see cref="DiscordGateway"/> is
		/// worth having rather than simply re-identifying every time.
		/// </remarks>
		sealed class SessionStartLimit
		{
			[JsonPropertyName("remaining")]
			public int Remaining { get; set; }

			[JsonPropertyName("reset_after")]
			public long ResetAfter { get; set; }
		}
	}

	/// <summary>
	/// Enough to find a message again later.
	/// </summary>
	/// <remarks>
	/// Both halves are needed: Discord's edit and delete endpoints are addressed by channel *and* message, and the
	/// channel is the rate limit's major parameter.
	/// </remarks>
	/// <param name="ChannelId">Channel the message is in.</param>
	/// <param name="MessageId">The message itself.</param>
	public sealed record DiscordMessageReference(string ChannelId, string MessageId);
}
