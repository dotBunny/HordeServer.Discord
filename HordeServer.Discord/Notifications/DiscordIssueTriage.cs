// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Globalization;
using EpicGames.Horde.Commits;
using HordeServer.Discord.Client;
using HordeServer.Issues;
using HordeServer.Users;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HordeServer.Discord.Notifications
{
	/// <summary>
	/// Turns triage button presses into changes on the issue.
	/// </summary>
	/// <remarks>
	/// The half that makes the buttons real. Everything before this - the gateway, the router, the components -
	/// carries a press from Discord to here; this is where it becomes an update to Horde's own issue database.
	///
	/// Two things shape the code. The first is that a press identifies its author with a **Discord snowflake**, and
	/// every issue operation is audited against a Horde user, so nothing can happen until the user map resolves them
	/// in reverse. An unmapped presser is told so rather than silently ignored - they are looking at the button and
	/// waiting for it to do something.
	///
	/// The second is that <c>markfixed</c> opens a modal, which Discord only permits as the *first* answer to an
	/// interaction. It is registered as answering for itself; every other verb is acknowledged by the router before
	/// it gets here. See <see cref="DiscordInteractionRouter"/>.
	/// </remarks>
	public sealed class DiscordIssueTriage : IHostedService
	{
		/// <summary>
		/// Custom id of the field the fix changelist is collected in.
		/// </summary>
		public const string FixCommitField = "fix_cl";

		/// <summary>
		/// Custom id of the root cause summary field, whose presence decides whether a category is asked for.
		/// </summary>
		public const string RootCauseSummaryField = "rootcause_summary";

		/// <summary>
		/// Custom id of the root cause changelist field.
		/// </summary>
		public const string RootCauseCommitField = "rootcause_cl";

		/// <summary>
		/// Custom id of the duplicate issue field.
		/// </summary>
		public const string DuplicateIssueField = "rootcause_dupeid";

		/// <summary>
		/// Horde's root cause vocabulary, as offered by the follow-up dropdown.
		/// </summary>
		/// <remarks>
		/// Free text in Horde's data model, and a controlled list in Slack's view. The list is kept here rather than
		/// read from configuration because it is a shared vocabulary - a category only means anything if everybody
		/// picks from the same set - and because Slack's is hardcoded too.
		/// </remarks>
		public static readonly IReadOnlyList<string> RootCauseCategories =
		[
			"Code", "Content", "Configuration", "Infrastructure", "Toolchain", "Flaky test", "Unknown",
		];

		readonly DiscordInteractionRouter _router;
		readonly IHordeIssues _issues;
		readonly IDiscordUserResolver _discordUsers;
		readonly IUserCollection _hordeUsers;
		readonly DiscordNotificationProcessor _processor;
		readonly ILogger _logger;

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="router">Router to register with.</param>
		/// <param name="issues">Issue operations.</param>
		/// <param name="discordUsers">Maps between Discord accounts and Horde users.</param>
		/// <param name="hordeUsers">Horde's user collection, for the reverse lookup.</param>
		/// <param name="processor">Builds the replacement message once an action has been taken.</param>
		/// <param name="logger">Logger for triage actions.</param>
		public DiscordIssueTriage(
			DiscordInteractionRouter router,
			IHordeIssues issues,
			IDiscordUserResolver discordUsers,
			IUserCollection hordeUsers,
			DiscordNotificationProcessor processor,
			ILogger<DiscordIssueTriage> logger)
		{
			_router = router;
			_issues = issues;
			_discordUsers = discordUsers;
			_hordeUsers = hordeUsers;
			_processor = processor;
			_logger = logger;
		}

		/// <inheritdoc/>
		public Task StartAsync(CancellationToken cancellationToken)
		{
			_router.Register(DiscordCustomId.IssueScope, HandleAsync, AnswersForItself);

			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

		/// <summary>
		/// Whether a verb produces the first response to its interaction itself.
		/// </summary>
		/// <remarks>
		/// Only the one that opens a modal. Discord refuses to attach a dialog to an interaction that has already
		/// been answered, and the router acknowledges everything else up front to beat the three-second deadline.
		/// </remarks>
		/// <param name="customId">Custom id of the component that was used.</param>
		public static bool AnswersForItself(DiscordCustomId customId)
			=> String.Equals(customId.Verb, "markfixed", StringComparison.Ordinal);

		/// <summary>
		/// Acts on one triage interaction.
		/// </summary>
		/// <param name="context">The interaction, already acknowledged unless the verb answers for itself.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public async Task HandleAsync(DiscordInteractionContext context, CancellationToken cancellationToken)
		{
			if (!Int32.TryParse(context.CustomId.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int issueId))
			{
				_logger.LogWarning("Discord triage component '{CustomId}' does not name an issue.", context.CustomId);
				return;
			}

			IUser? user = await FindHordeUserAsync(context, cancellationToken);

			if (user == null)
			{
				return;
			}

			_logger.LogInformation("Issue {IssueId}: {Verb} from Discord user {DiscordUserId} ({HordeUser})",
				issueId, context.CustomId.Verb, context.DiscordUserId, user.Name);

			switch (context.CustomId.Verb)
			{
				case "ack":
					await AcknowledgeAsync(context, issueId, user, cancellationToken);
					break;

				case "claim":
					await ApplyAsync(context, issueId, _issues.AcknowledgeAsync(issueId, user.Id, true, cancellationToken), cancellationToken);
					break;

				case "decline":
					await ApplyAsync(context, issueId, _issues.DeclineAsync(issueId, user.Id, cancellationToken), cancellationToken);
					break;

				case "markfixed":
					await OpenMarkFixedAsync(context, issueId, cancellationToken);
					break;

				case "fixsubmit":
					await SubmitMarkFixedAsync(context, issueId, user, cancellationToken);
					break;

				case "category":
					await SetCategoryAsync(context, issueId, user, cancellationToken);
					break;

				default:
					_logger.LogWarning("Nothing handles the Discord triage verb '{Verb}'.", context.CustomId.Verb);
					break;
			}
		}

		/// <summary>
		/// Acknowledges an issue, asking first if it would take it off somebody else.
		/// </summary>
		/// <remarks>
		/// In a direct message the reader is already the owner, so this is simply "yes, mine". In a channel it is
		/// how an unowned issue gets claimed - and if somebody else owns it, taking it silently would be wrong.
		/// Slack asks for confirmation in that case with a modal; here it is an ephemeral message with one button,
		/// which says the same thing without interrupting.
		/// </remarks>
		async Task AcknowledgeAsync(DiscordInteractionContext context, int issueId, IUser user, CancellationToken cancellationToken)
		{
			bool inChannel = context.Interaction.GuildId != null;
			IIssue? issue = await _issues.GetAsync(issueId, cancellationToken);

			if (issue == null)
			{
				await ReportAsync(context, $"Issue {issueId} no longer exists.", cancellationToken);
				return;
			}

			if (inChannel && issue.OwnerId != null && issue.OwnerId != user.Id)
			{
				IUser? owner = await _hordeUsers.GetCachedUserAsync(issue.OwnerId, cancellationToken);

				await ReportAsync(
					context,
					$"Issue {issueId} is currently assigned to **{DiscordMarkdown.Escape(owner?.Name ?? "somebody else")}**.",
					cancellationToken,
					new DiscordComponentBuilder().AddButton(
						new DiscordCustomId(DiscordCustomId.IssueScope, context.CustomId.Id, "claim").ToString(),
						"Take it anyway",
						DiscordButtonStyle.Danger));

				return;
			}

			// Acknowledging in a channel claims the issue; in a DM the reader already has it.
			await ApplyAsync(context, issueId, _issues.AcknowledgeAsync(issueId, user.Id, inChannel, cancellationToken), cancellationToken);
		}

		Task OpenMarkFixedAsync(DiscordInteractionContext context, int issueId, CancellationToken cancellationToken)
		{
			// Four of the five inputs a modal allows, which is what the whole hybrid flow is built around. The fifth
			// slot is deliberately left free rather than spent - see PLAN.md 3.3.4.
			DiscordModal modal = new DiscordModalBuilder(
					new DiscordCustomId(DiscordCustomId.IssueScope, context.CustomId.Id, "fixsubmit").ToString(),
					$"Mark issue {issueId} fixed")
				.AddTextInput(FixCommitField, "Fix changelist", required: true, placeholder: "12345")
				.AddTextInput(RootCauseSummaryField, "Root cause summary", paragraph: true,
					placeholder: "Fill this in to be asked for a category")
				.AddTextInput(RootCauseCommitField, "Root cause changelist")
				.AddTextInput(DuplicateIssueField, "Duplicate of issue")
				.Build();

			return _router.RespondAsync(context, DiscordInteractionResponse.OpenModal(modal), cancellationToken);
		}

		async Task SubmitMarkFixedAsync(DiscordInteractionContext context, int issueId, IUser user, CancellationToken cancellationToken)
		{
			IReadOnlyDictionary<string, string> values = context.Interaction.GetModalValues();
			string summary = values.GetValueOrDefault(RootCauseSummaryField, String.Empty);

			bool updated = await _issues.ResolveAsync(
				issueId,
				user.Id,
				ParseCommit(values.GetValueOrDefault(FixCommitField)),
				String.IsNullOrWhiteSpace(summary) ? null : summary,
				ParseCommit(values.GetValueOrDefault(RootCauseCommitField)),
				ParseIssueId(values.GetValueOrDefault(DuplicateIssueField)),
				cancellationToken);

			await UpdateMessageAsync(context, issueId, updated, cancellationToken);

			if (!updated || String.IsNullOrWhiteSpace(summary))
			{
				// The common path. Closing out a fix stays a single interaction unless somebody actually did root
				// cause analysis, which is the whole argument for the hybrid.
				return;
			}

			await _router.FollowUpAsync(
				context,
				new DiscordMessageBuilder()
					.WithContent($"Thanks - what kind of root cause was issue {issueId}?")
					.WithComponents(new DiscordComponentBuilder().AddSelect(
						new DiscordCustomId(DiscordCustomId.IssueScope, context.CustomId.Id, "category").ToString(),
						[.. RootCauseCategories.Select(x => new DiscordSelectOption { Label = x, Value = x })],
						"Pick a category"))
					.Build(),
				ephemeral: true,
				cancellationToken);
		}

		async Task SetCategoryAsync(DiscordInteractionContext context, int issueId, IUser user, CancellationToken cancellationToken)
		{
			string? category = context.Interaction.Data?.Values?.FirstOrDefault();

			if (String.IsNullOrEmpty(category))
			{
				return;
			}

			bool updated = await _issues.SetRootCauseCategoryAsync(issueId, user.Id, category, cancellationToken);

			await _router.UpdateMessageAsync(
				context,
				new DiscordMessageBuilder()
					.WithContent(updated
						? $"Recorded **{DiscordMarkdown.Escape(category)}** as the root cause of issue {issueId}."
						: $"Could not record a root cause category for issue {issueId}.")
					.WithoutComponents()
					.Build(),
				cancellationToken);
		}

		/// <summary>
		/// Runs an update and rewrites the message to match the issue afterwards.
		/// </summary>
		async Task ApplyAsync(DiscordInteractionContext context, int issueId, Task<bool> update, CancellationToken cancellationToken)
			=> await UpdateMessageAsync(context, issueId, await update, cancellationToken);

		/// <summary>
		/// Replaces the message the button was on with the issue as it now stands.
		/// </summary>
		/// <remarks>
		/// Through the interaction token, which is what makes this work on a direct message the bot could not
		/// otherwise edit, and on a channel message it did not post.
		/// </remarks>
		async Task UpdateMessageAsync(DiscordInteractionContext context, int issueId, bool updated, CancellationToken cancellationToken)
		{
			if (!updated)
			{
				await ReportAsync(context, $"Horde would not accept that change to issue {issueId}.", cancellationToken);
				return;
			}

			IIssue? issue = await _issues.GetAsync(issueId, cancellationToken);

			if (issue == null)
			{
				return;
			}

			await _router.UpdateMessageAsync(context, _processor.BuildIssueMessage(issue), cancellationToken);
		}

		/// <summary>
		/// Tells the person who pressed the button something, without disturbing the channel.
		/// </summary>
		async Task ReportAsync(DiscordInteractionContext context, string message, CancellationToken cancellationToken, DiscordComponentBuilder? components = null)
		{
			DiscordMessageBuilder builder = new DiscordMessageBuilder().WithContent(message);

			if (components != null)
			{
				builder.WithComponents(components);
			}

			await _router.FollowUpAsync(context, builder.Build(), ephemeral: true, cancellationToken);
		}

		/// <summary>
		/// Works out which Horde user pressed the button.
		/// </summary>
		async Task<IUser?> FindHordeUserAsync(DiscordInteractionContext context, CancellationToken cancellationToken)
		{
			string? email = _discordUsers.GetEmail(context.DiscordUserId);

			if (email == null)
			{
				await ReportAsync(
					context,
					"Your Discord account is not linked to a Horde user, so this cannot be recorded against you. "
					+ "Ask an administrator to add you to the plugin's `userMap`.",
					cancellationToken);

				return null;
			}

			IUser? user = await _hordeUsers.FindUserByEmailAsync(email, cancellationToken);

			if (user == null)
			{
				_logger.LogWarning("The userMap points Discord user {DiscordUserId} at '{Email}', which is not a "
					+ "Horde user.", context.DiscordUserId, email);

				await ReportAsync(context, $"No Horde user has the address `{email}` that you are mapped to.", cancellationToken);
			}

			return user;
		}

		/// <summary>
		/// Reads a changelist out of a text field.
		/// </summary>
		/// <remarks>
		/// Null for anything unparseable, including blank. Every one of these fields is optional except the fix
		/// changelist, and rejecting the whole submission because somebody typed "n/a" into an optional box would
		/// lose the fix along with it.
		/// </remarks>
		static CommitId? ParseCommit(string? value)
			=> String.IsNullOrWhiteSpace(value) ? null : new CommitId(value.Trim());

		static int? ParseIssueId(string? value)
			=> Int32.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : null;
	}
}
