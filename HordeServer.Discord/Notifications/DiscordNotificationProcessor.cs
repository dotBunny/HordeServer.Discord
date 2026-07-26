// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text.Json;
using EpicGames.Horde.Agents;
using EpicGames.Horde.Issues;
using EpicGames.Horde.Jobs;
using EpicGames.Horde.Jobs.Graphs;
using EpicGames.Horde.Logs;
using EpicGames.Horde.Streams;
using EpicGames.Horde.Users;
using HordeServer.Agents;
using HordeServer.Configuration;
using HordeServer.Devices;
using HordeServer.Discord.Client;
using HordeServer.Issues;
using HordeServer.Jobs.TestData;
using HordeServer.Logs;
using HordeServer.Notifications;
using HordeServer.Streams;
using HordeServer.Users;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HordeServer.Discord.Notifications
{
	/// <summary>
	/// Turns Horde notifications into Discord messages.
	/// </summary>
	/// <remarks>
	/// Separated from <see cref="DiscordNotificationSink"/> so the sink stays a thin, diff-friendly list of the
	/// interface's members and all the formatting lives somewhere it can be read on its own. This mirrors how the
	/// Experimental plugin splits its Slack sink from its processor. Members are grouped in the same order and under
	/// the same headings as the sink, so the two files can be read side by side.
	///
	/// Phases 1 and 2 cover everything that is a one-way broadcast: job and step outcomes, configuration updates,
	/// agent and device reports, and test health. Notifications aimed at a specific person still go to a channel with
	/// that person named in plain text - Discord needs a hand-maintained email-to-snowflake map before it can DM or
	/// mention anyone, which is Phase 3. Posting them unaddressed is the honest interim: the information arrives, it
	/// just is not routed yet. Issues and interactivity are Phase 4.
	/// </remarks>
	public sealed class DiscordNotificationProcessor
	{
		/// <summary>Discord's brand green, used for a clean outcome.</summary>
		public const int SuccessColor = 0x57F287;

		/// <summary>Discord's brand yellow, used for warnings.</summary>
		public const int WarningColor = 0xFEE75C;

		/// <summary>Discord's brand red, used for failures.</summary>
		public const int FailureColor = 0xED4245;

		/// <summary>Discord's "greyple", used when the outcome is not known.</summary>
		public const int NeutralColor = 0x99AAB5;

		/// <summary>
		/// How many log events are quoted on a failing step before the rest are summarised.
		/// </summary>
		/// <remarks>
		/// A broken compile can produce thousands. The embed limits would truncate them anyway; choosing the cut
		/// here means the message ends with a count and a link rather than a severed error message.
		/// </remarks>
		public const int MaxQuotedLogEvents = 5;

		/// <summary>
		/// How many steps or jobs are listed individually before the rest are summarised.
		/// </summary>
		public const int MaxListedItems = 10;

		readonly DiscordClient _client;
		readonly DiscordChannelResolver _channels;
		readonly IDiscordUserResolver _discordUsers;
		readonly DiscordRepeatFilter _repeats;
		readonly DiscordServerConfig _serverConfig;
		readonly BuildServerConfig _buildServerConfig;
		readonly IOptionsMonitor<BuildConfig> _buildConfig;
		readonly IUserCollection _hordeUsers;
		readonly IServerInfo _serverInfo;
		readonly ILogger _logger;

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="client">Client used to post.</param>
		/// <param name="channels">Works out where each notification goes.</param>
		/// <param name="discordUsers">Works out which Discord account belongs to a Horde user.</param>
		/// <param name="repeats">Suppresses re-announcing a condition that has not changed.</param>
		/// <param name="serverConfig">Server configuration, for the bot token and emoji prefixes.</param>
		/// <param name="buildServerConfig">Build plugin server configuration, to see whether Slack is also running.</param>
		/// <param name="buildConfig">Build plugin global configuration, for per-stream notification channels.</param>
		/// <param name="hordeUsers">Horde user lookup, for turning the user ids on a report into users.</param>
		/// <param name="serverInfo">Server information, for dashboard links.</param>
		/// <param name="logger">Logger for delivery problems.</param>
		public DiscordNotificationProcessor(DiscordClient client, DiscordChannelResolver channels, IDiscordUserResolver discordUsers, DiscordRepeatFilter repeats, IOptions<DiscordServerConfig> serverConfig, IOptions<BuildServerConfig> buildServerConfig, IOptionsMonitor<BuildConfig> buildConfig, IUserCollection hordeUsers, IServerInfo serverInfo, ILogger<DiscordNotificationProcessor> logger)
		{
			_client = client;
			_channels = channels;
			_discordUsers = discordUsers;
			_repeats = repeats;
			_serverConfig = serverConfig.Value;
			_buildServerConfig = buildServerConfig.Value;
			_buildConfig = buildConfig;
			_hordeUsers = hordeUsers;
			_serverInfo = serverInfo;
			_logger = logger;
		}

		/// <summary>
		/// Whether there is both a way to send job notifications and somewhere to send them.
		/// </summary>
		/// <remarks>
		/// The plugin registers its sink whether or not it is configured, so this is the real gate. Running it
		/// unconfigured is a supported way to verify the plugin loads before any Discord credentials exist.
		///
		/// Resolved on each call rather than cached, because the channel map is hot-reloadable: adding a mapping
		/// should start delivery without a server restart.
		/// </remarks>
		public bool CanSendJobNotifications
			=> _serverConfig.IsConfigured && _channels.ResolveCategory(DiscordChannelCategory.Job).Count > 0;

		#region Jobs

		/// <summary>
		/// Reports to a channel that a job finished.
		/// </summary>
		/// <param name="job">Job that finished.</param>
		/// <param name="outcome">How it went.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public Task NotifyJobCompleteAsync(IJob job, LabelOutcome outcome, CancellationToken cancellationToken)
			// Routed by the job and its stream rather than the base category, which is what Horde itself does for
			// completions - and the only path that honours a per-template or per-stream notification channel.
			=> SendAsync(
				_channels.ResolveJobCompletion(job, GetStreamConfig(job.StreamId), outcome),
				BuildJobCompleteEmbed(job, outcome),
				null,
				cancellationToken);

		/// <summary>
		/// Tells one person that a job they subscribed to finished.
		/// </summary>
		/// <remarks>
		/// A direct message, matching Slack - this is the *subscription* notification, and it is addressed to one
		/// person rather than announced. Somebody who cannot be reached directly gets it in the job channel instead,
		/// which is the whole reason the fallback exists: an unmapped user must cost a mention, never a notification.
		///
		/// The person who aborted the job is skipped. They already know, and Slack skips them too.
		/// </remarks>
		/// <param name="user">User to tell.</param>
		/// <param name="job">Job that finished.</param>
		/// <param name="outcome">How it went.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public Task NotifyJobCompleteToUserAsync(IUser user, IJob job, LabelOutcome outcome, CancellationToken cancellationToken)
		{
			if (job.AbortedByUserId == user.Id)
			{
				return Task.CompletedTask;
			}

			return SendToUsersAsync(
				Only(user),
				_channels.ResolveCategory(DiscordChannelCategory.Job),
				BuildJobCompleteEmbed(job, outcome),
				cancellationToken);
		}

		DiscordEmbedBuilder BuildJobCompleteEmbed(IJob job, LabelOutcome outcome)
		{
			DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
				.WithTitle($"{Prefix(outcome)}{job.Name}")
				.WithUrl(GetJobUrl(job.Id).ToString())
				.WithColor(GetColor(outcome))
				.WithTimestamp(job.UpdateTimeUtc);

			AddJobContext(embed, job);
			embed.AddField("Outcome", Describe(outcome), true);

			return embed;
		}

		/// <summary>
		/// Tells the people subscribed to a step that it finished.
		/// </summary>
		/// <remarks>
		/// Direct messages, matching Slack: these are subscription notifications, and broadcasting one to a channel
		/// per subscriber would make the job channel unusable on a busy stream. Anyone unreachable is named in the
		/// job channel instead.
		///
		/// A step that timed out is reported to the job channel as well, whether or not anyone subscribed. **This is
		/// a deliberate difference from Slack**, which checks for subscribers first and so never reports a timeout on
		/// a step nobody was watching - an ordering accident rather than an intention, since its timeout branch does
		/// not look at the subscriber list at all. A step hitting its time limit is a farm problem.
		/// </remarks>
		/// <param name="job">Job containing the step.</param>
		/// <param name="step">Step that finished.</param>
		/// <param name="node">Node the step ran.</param>
		/// <param name="events">Log events produced by the step.</param>
		/// <param name="usersToNotify">Users subscribed to the step.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public async Task NotifyJobStepCompleteAsync(IJob job, IJobStep step, INode node, IReadOnlyList<ILogEventData> events, IEnumerable<IUser>? usersToNotify, CancellationToken cancellationToken)
		{
			if (step.Error == JobStepError.TimedOut)
			{
				DiscordEmbedBuilder timedOut = BuildStepEmbed(job, step, node, events, FailureColor, _serverConfig.ErrorPrefix, "Timed out");

				await SendAsync(_channels.ResolveCategory(DiscordChannelCategory.Job), timedOut, null, cancellationToken);
			}

			if (!HasAny(usersToNotify))
			{
				return;
			}

			await SendToUsersAsync(
				usersToNotify,
				_channels.ResolveCategory(DiscordChannelCategory.Job),
				BuildStepEmbed(job, step, node, events, GetColor(step.Outcome), Prefix(step.Outcome), Describe(step.Outcome)),
				cancellationToken);
		}

		/// <summary>
		/// Tells the people subscribed to a step that it was aborted.
		/// </summary>
		/// <remarks>
		/// Slack implements this member as a no-op, so anything here is additive. It is worth sending because the
		/// cancellation reason is the part people actually want and is easy to miss in the dashboard - but it stays
		/// addressed to subscribers rather than broadcast, so the addition cannot become noise.
		/// </remarks>
		/// <param name="job">Job containing the step.</param>
		/// <param name="step">Step that was aborted.</param>
		/// <param name="node">Node the step was running.</param>
		/// <param name="events">Log events produced before the abort.</param>
		/// <param name="usersToNotify">Users subscribed to the step.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public Task NotifyJobStepAbortedAsync(IJob job, IJobStep step, INode node, IReadOnlyList<ILogEventData> events, IEnumerable<IUser>? usersToNotify, CancellationToken cancellationToken)
		{
			if (!HasAny(usersToNotify))
			{
				return Task.CompletedTask;
			}

			// An abort is not a failure - somebody chose it - so it gets the neutral colour and says why.
			string reason = step.CancellationReason ?? job.CancellationReason ?? "Aborted";

			return SendToUsersAsync(
				usersToNotify,
				_channels.ResolveCategory(DiscordChannelCategory.Job),
				BuildStepEmbed(job, step, node, events, NeutralColor, _serverConfig.WarningPrefix, reason),
				cancellationToken);
		}

		/// <summary>
		/// Tells one person that a label they subscribed to finished.
		/// </summary>
		/// <remarks>A direct message, matching Slack, falling back to the job channel for an unreachable user.</remarks>
		/// <param name="job">Job the label belongs to.</param>
		/// <param name="label">Label that finished.</param>
		/// <param name="outcome">How it went.</param>
		/// <param name="stepData">Name, outcome and link for each step in the label.</param>
		/// <param name="forUser">User to tell.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public Task NotifyLabelCompleteAsync(IJob job, ILabel label, LabelOutcome outcome, IReadOnlyList<(string Name, JobStepOutcome Outcome, Uri Url)> stepData, IUser forUser, CancellationToken cancellationToken)
		{
			string name = label.DashboardName ?? label.UgsName ?? "Label";

			DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
				.WithTitle($"{Prefix(outcome)}{name}")
				.WithUrl(GetJobUrl(job.Id).ToString())
				.WithColor(GetColor(outcome))
				.WithTimestamp(job.UpdateTimeUtc);

			AddJobContext(embed, job);

			// Only the steps that went wrong are worth listing. On a healthy label that is none of them, and the
			// embed stays a one-liner; on a broken one it is the answer to "which part?".
			IReadOnlyList<(string Name, JobStepOutcome Outcome, Uri Url)> notable =
				[.. stepData.Where(x => x.Outcome != JobStepOutcome.Success)];

			if (notable.Count > 0)
			{
				embed.AddField($"Steps ({notable.Count})", Summarise(notable, x => $"{Prefix(x.Outcome).TrimEnd()} [{Escape(x.Name)}]({x.Url})".TrimStart()));
			}

			return SendToUsersAsync(Only(forUser), _channels.ResolveCategory(DiscordChannelCategory.Job), embed, cancellationToken);
		}

		/// <summary>
		/// Reports that jobs are waiting to be scheduled.
		/// </summary>
		/// <param name="notifications">Jobs that are waiting, and the pools they are waiting on.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public Task NotifyJobScheduledAsync(IReadOnlyList<JobScheduledNotification> notifications, CancellationToken cancellationToken)
		{
			if (notifications.Count == 0)
			{
				return Task.CompletedTask;
			}

			DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
				.WithTitle($"{_serverConfig.WarningPrefix}{notifications.Count} job(s) waiting to be scheduled")
				.WithColor(WarningColor)
				.WithTimestamp(DateTimeOffset.UtcNow);

			// Grouped by pool because that is the actionable unit: jobs pile up when one pool has no agents, and a
			// flat list of twenty job names buries which pool is the problem.
			foreach (IGrouping<string, JobScheduledNotification> pool in notifications.GroupBy(x => x.PoolName).Take(MaxListedItems))
			{
				IReadOnlyList<JobScheduledNotification> jobs = [.. pool];

				embed.AddField(
					$"{pool.Key} ({jobs.Count})",
					Summarise(jobs, x => $"[{Escape(x.JobName)}]({GetJobUrl(JobId.Parse(x.JobId))})"));
			}

			return SendAsync(_channels.ResolveCategory(DiscordChannelCategory.Job), embed, null, cancellationToken);
		}

		DiscordEmbedBuilder BuildStepEmbed(IJob job, IJobStep step, INode node, IReadOnlyList<ILogEventData> events, int color, string prefix, string outcome)
		{
			DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
				.WithTitle($"{prefix}{node.Name}")
				.WithUrl(GetStepUrl(job.Id, step.Id).ToString())
				.WithColor(color)
				.WithTimestamp(step.FinishTimeUtc ?? job.UpdateTimeUtc)
				.AddField("Job", $"[{Escape(job.Name)}]({GetJobUrl(job.Id)})")
				.AddField("Outcome", outcome, true);

			AddJobContext(embed, job);
			AddDuration(embed, step);
			AddLogEvents(embed, events);

			return embed;
		}

		void AddJobContext(DiscordEmbedBuilder embed, IJob job)
		{
			embed.AddField("Stream", job.StreamId.ToString(), true);
			embed.AddField("Change", job.CommitId.Name, true);

			if (job.PreflightCommitId != null)
			{
				embed.AddField("Preflight", job.PreflightCommitId.Name, true);
			}
		}

		static void AddDuration(DiscordEmbedBuilder embed, IJobStep step)
		{
			if (step.StartTimeUtc is DateTime started && step.FinishTimeUtc is DateTime finished && finished > started)
			{
				embed.AddField("Duration", FormatDuration(finished - started), true);
			}
		}

		void AddLogEvents(DiscordEmbedBuilder embed, IReadOnlyList<ILogEventData> events)
		{
			// Information-level events are noise here; whoever is reading this wants the reason it went red.
			IReadOnlyList<ILogEventData> notable =
				[.. events.Where(x => x.Severity is LogEventSeverity.Error or LogEventSeverity.Warning)];

			if (notable.Count == 0)
			{
				return;
			}

			// Both markers have to come from the emoji set or they render as different kinds of thing. The pair here
			// was ✘ / ⚠, which looks symmetrical in source and is not: Twemoji has an image for U+26A0 whether or not
			// it carries a variation selector, and none for U+2718, so the list came out a colour emoji beside a
			// monochrome text glyph. Circles also match the vocabulary the device reports below already use.
			embed.AddField(
				$"Events ({notable.Count})",
				Summarise(
					notable,
					x => $"{(x.Severity == LogEventSeverity.Error ? "🔴" : "🟡")} {Escape(FirstLine(x.Message))}",
					MaxQuotedLogEvents,
					"see the log for the rest"));
		}

		#endregion

		#region Issues

		/// <summary>
		/// Most issues listed in one report embed before the rest are counted instead.
		/// </summary>
		/// <remarks>
		/// Slack uses eight per message. The same number here, for a different reason: an embed holds 25 fields, but
		/// a digest that needs scrolling is one nobody reads.
		/// </remarks>
		public const int MaxIssuesPerReport = 8;

		/// <summary>
		/// Announces a change to an issue, with the buttons to act on it.
		/// </summary>
		/// <remarks>
		/// Addressed to the person who can do something about it - the owner if one has been assigned, the nominee
		/// if somebody has been suggested - and falling back to the triage channel when neither is reachable. That
		/// is the same rule as the subscription notifications in Phase 3, and for the same reason: this is a request
		/// to act, not an announcement.
		///
		/// **Repeated states are suppressed rather than reposted.** Horde raises this on every change to an issue,
		/// including ones that alter nothing a reader would notice, and an issue open for a day would otherwise
		/// produce a wall of near-identical messages. The digest in <see cref="DescribeIssueState"/> is what counts
		/// as a change.
		///
		/// **Not yet edit-in-place.** A state change posts a new message rather than rewriting the old one, because
		/// remembering which message belongs to which issue across a restart needs the Mongo collection that is
		/// still deferred - see <c>.claude/PLAN.md</c> section 3.3.6. The buttons work regardless: a press carries
		/// its own interaction token, and the message it is on is edited through that.
		/// </remarks>
		/// <param name="issue">Issue that changed.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public async Task NotifyIssueUpdatedAsync(IIssue issue, CancellationToken cancellationToken)
		{
			if (!_serverConfig.IsConfigured)
			{
				return;
			}

			// Quarantining an issue is how an operator says "stop telling people about this".
			if (issue.QuarantinedByUserId != null)
			{
				_logger.LogDebug("Issue {IssueId} is quarantined; not notifying Discord.", issue.Id);
				return;
			}

			if (!_repeats.RecordIfChanged(IssueEventId(issue), DescribeIssueState(issue)))
			{
				_logger.LogDebug("Issue {IssueId} has not changed in any way worth re-posting to Discord.", issue.Id);
				return;
			}

			UserId? recipientId = issue.OwnerId ?? issue.NominatedById;
			IUser? recipient = recipientId == null ? null : await GetUserAsync(recipientId.Value, cancellationToken);
			IReadOnlyList<DiscordDestination> triage = ResolveIssueChannels(issue);

			DiscordEmbedBuilder embed = BuildIssueEmbed(issue);
			DiscordComponentBuilder buttons = BuildIssueButtons(issue);

			if (recipient == null)
			{
				await SendAsync(triage, embed, null, buttons, cancellationToken);
				return;
			}

			await SendToUsersAsync([recipient], triage, embed, buttons, cancellationToken);
		}

		/// <summary>
		/// Posts the periodic summary of everything open in a workflow.
		/// </summary>
		/// <remarks>
		/// One embed per stream and workflow, all to the channel Horde chose. Unlike
		/// <see cref="NotifyIssueUpdatedAsync"/> this is a digest rather than a request to act, so it carries no
		/// buttons and is never sent as a direct message.
		/// </remarks>
		/// <param name="group">Reports to post, and the channel they belong in.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public async Task SendIssueReportAsync(IssueReportGroup group, CancellationToken cancellationToken)
		{
			if (!_serverConfig.IsConfigured)
			{
				return;
			}

			DiscordDestination? destination = _channels.Resolve(group.Channel);

			if (destination == null)
			{
				_logger.LogDebug("No Discord channel is mapped for issue report channel {Channel}.", group.Channel);
				return;
			}

			// Ordered the way Slack orders them, so a studio reading both sees the same sequence.
			foreach (IssueReport report in group.Reports.OrderBy(x => x.WorkflowId.ToString(), StringComparer.Ordinal)
				.ThenBy(x => x.StreamId.ToString(), StringComparer.Ordinal))
			{
				await SendToAsync(destination, BuildIssueReportEmbed(report, group.Time), null, cancellationToken);
			}
		}

		/// <summary>
		/// Builds the whole message for an issue - embed, buttons and all.
		/// </summary>
		/// <remarks>
		/// Public because triage rewrites the message after acting on it, and it has to render the issue the same
		/// way this class first posted it. Anything else and a message would change shape the moment somebody
		/// pressed a button on it.
		/// </remarks>
		/// <param name="issue">Issue to render.</param>
		/// <returns>A message ready to post or to replace an existing one with.</returns>
		public DiscordMessage BuildIssueMessage(IIssue issue)
		{
			DiscordComponentBuilder buttons = BuildIssueButtons(issue);
			DiscordMessageBuilder message = new DiscordMessageBuilder().AddEmbed(BuildIssueEmbed(issue));

			// A resolved issue has only its link left, and an edit that omitted components entirely would leave the
			// old buttons in place - so this always says explicitly which of the two it means.
			return buttons.IsEmpty
				? message.WithoutComponents().Build()
				: message.WithComponents(buttons).Build();
		}

		/// <summary>
		/// Builds the embed describing one issue.
		/// </summary>
		DiscordEmbedBuilder BuildIssueEmbed(IIssue issue)
		{
			string summary = String.IsNullOrEmpty(issue.UserSummary) ? issue.Summary : issue.UserSummary;

			DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
				.WithTitle($"{IssuePrefix(issue)}Issue {issue.Id}: {Escape(summary)}")
				.WithUrl(GetIssueUrl(issue.Id).ToString())
				.WithColor(IssueColor(issue));

			if (!String.IsNullOrEmpty(issue.Description) && issue.Description != summary)
			{
				embed.WithDescription(Escape(issue.Description));
			}

			embed.AddField("Status", DescribeIssueStatus(issue), true);
			embed.AddField("Severity", issue.Severity.ToString(), true);
			embed.AddField("Opened", $"<t:{new DateTimeOffset(issue.CreatedAt, TimeSpan.Zero).ToUnixTimeSeconds()}:R>", true);

			IReadOnlyList<string> streams = [.. issue.Streams.Select(x => x.StreamId.ToString()).Distinct(StringComparer.Ordinal)];

			if (streams.Count > 0)
			{
				embed.AddField(streams.Count == 1 ? "Stream" : "Streams", Escape(String.Join(", ", streams)), true);
			}

			if (issue.FixCommitId != null)
			{
				embed.AddField("Fixed in", Code(issue.FixCommitId.ToString()!), true);
			}

			if (!String.IsNullOrEmpty(issue.RootCauseCategory))
			{
				embed.AddField("Root cause", Escape(issue.RootCauseCategory), true);
			}

			return embed;
		}

		/// <summary>
		/// Builds the triage buttons for an issue.
		/// </summary>
		/// <remarks>
		/// Verbs kept from Slack - <c>ack</c>, <c>decline</c>, <c>markfixed</c> - so the two sinks are describing the
		/// same actions. A resolved issue offers nothing but the link: there is no state left to move it to, and a
		/// button that does nothing is worse than no button.
		/// </remarks>
		DiscordComponentBuilder BuildIssueButtons(IIssue issue)
		{
			DiscordComponentBuilder buttons = new DiscordComponentBuilder();
			string id = issue.Id.ToString(CultureInfo.InvariantCulture);

			if (issue.ResolvedAt == null)
			{
				if (issue.AcknowledgedAt == null)
				{
					buttons.AddButton(
						new DiscordCustomId(DiscordCustomId.IssueScope, id, "ack").ToString(),
						"Acknowledge",
						DiscordButtonStyle.Success);
				}

				buttons.AddButton(
					new DiscordCustomId(DiscordCustomId.IssueScope, id, "decline").ToString(),
					"Not me",
					DiscordButtonStyle.Secondary);

				buttons.AddButton(
					new DiscordCustomId(DiscordCustomId.IssueScope, id, "markfixed").ToString(),
					"Mark Fixed",
					DiscordButtonStyle.Primary);
			}

			buttons.AddLink(GetIssueUrl(issue.Id).ToString(), "Open in Horde");

			return buttons;
		}

		/// <summary>
		/// Builds the embed summarising one stream's workflow.
		/// </summary>
		DiscordEmbedBuilder BuildIssueReportEmbed(IssueReport report, DateTime time)
		{
			DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
				.WithTitle($"{Escape(report.StreamId.ToString())} - {Escape(report.WorkflowId.ToString())}")
				.WithColor(report.Issues.Count == 0 ? SuccessColor : WarningColor)
				.WithFooter($"as of {time:u}");

			WorkflowStats stats = report.WorkflowStats;

			if (stats.NumSteps > 0)
			{
				int percent = (int)Math.Round(100.0 * stats.NumPassingSteps / stats.NumSteps);
				embed.AddField("Steps passing", $"{stats.NumPassingSteps} of {stats.NumSteps} ({percent}%)", true);
			}

			if (report.Issues.Count == 0)
			{
				embed.WithDescription("No open issues.");
				return embed;
			}

			embed.AddField("Open issues", report.Issues.Count.ToString(CultureInfo.InvariantCulture), true);
			embed.AddField(
				"Issues",
				Summarise(
					[.. report.Issues.OrderBy(x => x.Id)],
					x => $"{IssueBullet(x)} [Issue {x.Id}]({GetIssueUrl(x.Id)}) {Escape(FirstLine(String.IsNullOrEmpty(x.UserSummary) ? x.Summary : x.UserSummary))}",
					MaxIssuesPerReport,
					"see the dashboard for the rest"));

			return embed;
		}

		/// <summary>
		/// Where an issue notification goes when nobody can be reached directly.
		/// </summary>
		/// <remarks>
		/// The triage channel of every workflow the issue's streams define one for, since an issue can span streams
		/// and each may triage separately. Falls back to the stream's own triage channel, then to the job channel,
		/// which is what <see cref="DiscordChannelResolver"/> does with anything unmapped.
		/// </remarks>
		IReadOnlyList<DiscordDestination> ResolveIssueChannels(IIssue issue)
		{
			List<string> channels = new List<string>();
			BuildConfig buildConfig = _buildConfig.CurrentValue;

			foreach (IIssueStream stream in issue.Streams)
			{
				if (!buildConfig.TryGetStream(stream.StreamId, out StreamConfig? streamConfig))
				{
					continue;
				}

				foreach (WorkflowConfig workflow in streamConfig.Workflows)
				{
					if (!String.IsNullOrEmpty(workflow.TriageChannel))
					{
						channels.Add(workflow.TriageChannel);
					}
				}

				if (!String.IsNullOrEmpty(streamConfig.TriageChannel))
				{
					channels.Add(streamConfig.TriageChannel);
				}
			}

			return channels.Count > 0
				? _channels.ResolveAll(channels.Distinct(StringComparer.Ordinal))
				: _channels.ResolveCategory(DiscordChannelCategory.Job);
		}

		/// <summary>
		/// Event id an issue's last announced state is remembered under.
		/// </summary>
		static string IssueEventId(IIssue issue) => $"issue:{issue.Id}";

		/// <summary>
		/// What counts as a change worth announcing.
		/// </summary>
		/// <remarks>
		/// The fields a reader would notice, and nothing else. <c>LastSeenAt</c> and <c>UpdateIndex</c> are
		/// deliberately absent: both move whenever the issue is touched, and including either would defeat the
		/// suppression entirely.
		/// </remarks>
		static string DescribeIssueState(IIssue issue)
			=> String.Join(
				'|',
				issue.Severity,
				issue.OwnerId?.ToString() ?? "-",
				issue.NominatedById?.ToString() ?? "-",
				issue.AcknowledgedAt?.ToString("O") ?? "-",
				issue.ResolvedAt?.ToString("O") ?? "-",
				issue.VerifiedAt?.ToString("O") ?? "-",
				issue.FixCommitId?.ToString() ?? "-",
				issue.RootCauseCategory ?? "-",
				issue.UserSummary ?? issue.Summary);

		/// <summary>
		/// One-line description of where an issue has got to.
		/// </summary>
		static string DescribeIssueStatus(IIssue issue)
		{
			if (issue.VerifiedAt != null)
			{
				return "Verified";
			}

			// Not "Fixed in X" - the commit has a field of its own, and saying it twice in one embed reads as two
			// different facts.
			if (issue.ResolvedAt != null)
			{
				return "Resolved";
			}

			if (issue.AcknowledgedAt != null)
			{
				return "Acknowledged";
			}

			return issue.OwnerId != null ? "Assigned" : "Unassigned";
		}

		static int IssueColor(IIssue issue)
		{
			if (issue.ResolvedAt != null)
			{
				return SuccessColor;
			}

			return issue.Severity == IssueSeverity.Warning ? WarningColor : FailureColor;
		}

		string IssuePrefix(IIssue issue)
		{
			if (issue.ResolvedAt != null)
			{
				return String.Empty;
			}

			return issue.Severity == IssueSeverity.Warning ? _serverConfig.WarningPrefix : _serverConfig.ErrorPrefix;
		}

		static string IssueBullet(IIssue issue)
		{
			if (issue.ResolvedAt != null)
			{
				return "🟢";
			}

			return issue.Severity == IssueSeverity.Warning ? "🟡" : "🔴";
		}

		Uri GetIssueUrl(int issueId) => new Uri(_serverInfo.DashboardUrl, $"issue/{issueId}");

		#endregion

		#region Configuration

		/// <summary>
		/// Event id under which configuration update failures are remembered.
		/// </summary>
		/// <remarks>
		/// One id for the whole server, because there is one configuration and it is either loading or it is not.
		/// </remarks>
		const string ConfigUpdateEventId = "config-update";

		/// <summary>
		/// Reports the outcome of a configuration update.
		/// </summary>
		/// <remarks>
		/// Horde re-reads its configuration on a ticker, so this fires on every pass - including every pass while a
		/// bad file stays bad. Only a *change* is posted: the same failure is announced once, and the success that
		/// follows is announced only if a failure was announced before it. Without that, one unclosed brace fills the
		/// channel, and "configuration update succeeded" every few minutes trains everyone to ignore it.
		/// </remarks>
		/// <param name="info">Outcome of the update, and the authors involved.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public Task NotifyConfigUpdateAsync(ConfigUpdateInfo info, CancellationToken cancellationToken)
		{
			IReadOnlyList<DiscordDestination> destinations = _channels.ResolveCategory(DiscordChannelCategory.Config);

			if (destinations.Count == 0)
			{
				return Task.CompletedTask;
			}

			if (info.Exception == null)
			{
				return NotifyConfigRecoveredAsync(destinations, info, cancellationToken);
			}

			DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
				.WithTitle($"{_serverConfig.ErrorPrefix}Configuration update failed")
				.WithColor(FailureColor)
				.WithTimestamp(DateTimeOffset.UtcNow);

			// The identity of the failure, as distinct from how it is presented. Two updates that fail the same way
			// in the same file are the same news however the message ends up worded.
			string state = $"{info.Exception.GetType().FullName}\n{info.Exception.Message}";

			embed.AddField("Error", CodeBlock(info.Exception.Message));

			if (info.Exception is ConfigException configException)
			{
				state += AddConfigBlame(embed, configException);
			}

			if (!_repeats.RecordIfChanged(ConfigUpdateEventId, state))
			{
				_logger.LogDebug("Configuration update is still failing in the same way; not re-posting to Discord.");
				return Task.CompletedTask;
			}

			return SendAsync(destinations, embed, null, cancellationToken);
		}

		/// <summary>
		/// Adds the file, revision and author a configuration error can be blamed on, and returns them for the digest.
		/// </summary>
		/// <remarks>
		/// This is the whole value of the notification. "Configuration failed to load" sends someone to the server
		/// log; "this file, at this revision, by this person, on this line" sends them to the fix.
		/// </remarks>
		/// <param name="embed">Embed to add to.</param>
		/// <param name="exception">Exception carrying the parse context.</param>
		/// <returns>Text describing the blame, for change detection.</returns>
		static string AddConfigBlame(DiscordEmbedBuilder embed, ConfigException exception)
		{
			ConfigContext context = exception.GetContext();

			if (!context.IncludeStack.TryPeek(out IConfigFile? blame))
			{
				return String.Empty;
			}

			// The include stack is a stack, so it reads innermost-first - which is the order the reader wants, since
			// the file at the top is the one to open.
			IReadOnlyList<IConfigFile> stack = [.. context.IncludeStack];

			string file = blame.GetUserFormattedPath();

			// System.Text.Json reports where it gave up, which is almost always where the mistake is.
			string line = exception.InnerException is JsonException { LineNumber: long lineNumber }
				? $" (line {lineNumber})"
				: String.Empty;

			embed.AddField("File", $"{Code(file)}{line}");

			if (blame.Author?.Name is string author && !String.IsNullOrEmpty(author))
			{
				embed.AddField("Last changed by", Escape(author), true);
			}

			if (stack.Count > 1)
			{
				embed.AddField("Include stack", CodeBlock(String.Join("\n", stack.Select(x => x.GetUserFormattedPath()))));
			}

			return $"\n{file}{line}";
		}

		Task NotifyConfigRecoveredAsync(IReadOnlyList<DiscordDestination> destinations, ConfigUpdateInfo info, CancellationToken cancellationToken)
		{
			// Nothing to correct means nobody was told it was broken, and a channel does not need to hear that the
			// configuration loaded - that is the normal state of the world.
			if (!_repeats.Clear(ConfigUpdateEventId))
			{
				return Task.CompletedTask;
			}

			DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
				.WithTitle("Configuration update succeeded")
				.WithColor(SuccessColor)
				.WithTimestamp(DateTimeOffset.UtcNow);

			if (info.Status.Count > 0)
			{
				embed.AddField("Status", Summarise(info.Status, Escape));
			}

			return SendAsync(destinations, embed, null, cancellationToken);
		}

		/// <summary>
		/// Reports that a stream's configuration could not be updated.
		/// </summary>
		/// <remarks>
		/// Distinct from <see cref="NotifyConfigUpdateAsync"/>: this one is raised per file by whatever was trying to
		/// read it, arrives with the commit and author already worked out, and is not repeated on a ticker - so it is
		/// posted every time rather than filtered.
		///
		/// Goes to the channel *and* to the author, which is the one place both are right. The channel is how the
		/// team learns the configuration is stale; the direct message is how the person who broke it finds out
		/// without watching a channel. Slack does the same.
		/// </remarks>
		/// <param name="errorMessage">What went wrong.</param>
		/// <param name="fileName">File that could not be read.</param>
		/// <param name="change">Commit that probably caused it.</param>
		/// <param name="author">Author of that commit.</param>
		/// <param name="description">Description of that commit.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public async Task NotifyConfigUpdateFailureAsync(string errorMessage, string fileName, int? change, IUser? author, string? description, CancellationToken cancellationToken)
		{
			DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
				.WithTitle($"{_serverConfig.ErrorPrefix}Stream configuration update failed")
				.WithColor(FailureColor)
				.WithTimestamp(DateTimeOffset.UtcNow)
				.AddField("File", Code(fileName))
				.AddField("Error", CodeBlock(errorMessage));

			if (change != null)
			{
				embed.AddField(
					"Possibly due to",
					author == null ? $"CL {change}" : $"CL {change} by {Escape(author.Name)}",
					true);

				if (!String.IsNullOrWhiteSpace(description))
				{
					embed.AddField("Description", CodeBlock(description));
				}
			}

			await SendAsync(
				_channels.ResolveCategory(DiscordChannelCategory.UpdateStreams),
				embed,
				Only(author),
				cancellationToken);

			// Best effort, and no fallback: the channel post above has already carried it, so an author who cannot be
			// reached directly has lost nothing.
			if (author != null)
			{
				await TrySendDirectAsync(author, embed.Build(), cancellationToken);
			}
		}

		#endregion

		#region Farm operations

		/// <summary>
		/// Reports something the device service wants a person to know.
		/// </summary>
		/// <remarks>
		/// A direct message, as Slack sends it - these are private reminders about a device checkout rather than
		/// anything the team needs. Where Slack sends nothing at all if it cannot identify the user, this falls back
		/// to the device channel naming them, so a gap in the user map costs a reminder its privacy rather than its
		/// existence.
		/// </remarks>
		/// <param name="message">Message from the device service.</param>
		/// <param name="device">Device it concerns.</param>
		/// <param name="pool">Pool the device belongs to.</param>
		/// <param name="streamConfig">Stream the job belongs to.</param>
		/// <param name="job">Job that was using the device.</param>
		/// <param name="step">Step that was using the device.</param>
		/// <param name="node">Node the step ran.</param>
		/// <param name="user">User this concerns.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public Task NotifyDeviceServiceAsync(string message, IDevice? device, IDevicePool? pool, StreamConfig? streamConfig, IJob? job, IJobStep? step, INode? node, IUser? user, CancellationToken cancellationToken)
		{
			DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
				.WithTitle("Device service")

				// Not escaped. These messages are composed by Horde rather than typed by anyone, and they carry a
				// dashboard URL - escaping would break the link to protect against markdown that is not there.
				.WithDescription(message)
				.WithColor(NeutralColor)
				.WithTimestamp(DateTimeOffset.UtcNow);

			if (device != null)
			{
				embed.AddField("Device", Escape(device.Name), true);
			}

			if (pool != null)
			{
				embed.AddField("Pool", Escape(pool.Name), true);
			}

			if (streamConfig != null)
			{
				embed.AddField("Stream", Escape(streamConfig.Name), true);
			}

			if (job != null)
			{
				embed.AddField("Job", $"[{Escape(job.Name)}]({GetJobUrl(job.Id)})");

				if (step != null && node != null)
				{
					embed.AddField("Step", $"[{Escape(node.Name)}]({GetStepUrl(job.Id, step.Id)})");
				}
			}

			return SendToUsersAsync(Only(user), _channels.ResolveCategory(DiscordChannelCategory.Device), embed, cancellationToken);
		}

		/// <summary>
		/// Reports the state of the device pools and any devices causing trouble.
		/// </summary>
		/// <remarks>
		/// Two reports in one call, and they answer different questions - "is there enough hardware?" and "which
		/// boxes are broken?" - so they go out as separate messages rather than one long embed, one per pool and one
		/// per platform. That also keeps each message well inside the combined embed ceiling on a farm large enough
		/// for this report to matter.
		/// </remarks>
		/// <param name="report">Report to send.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public async Task SendDeviceIssueReportAsync(DeviceIssueReport report, CancellationToken cancellationToken)
		{
			DiscordDestination? destination = _channels.Resolve(report.Channel);

			if (destination == null)
			{
				return;
			}

			int reported = 0;

			foreach (DevicePoolReport pool in report.PoolReports)
			{
				DiscordEmbedBuilder? embed = BuildPoolHealthEmbed(pool);

				if (embed != null)
				{
					reported++;
					await SendToAsync(destination, embed, null, cancellationToken);
				}
			}

			// Said explicitly rather than by saying nothing. A report that arrives every hour and then does not is
			// indistinguishable from a broken sink, and this is the one line that tells the two apart.
			if (report.PoolReports.Count > 0 && reported == 0)
			{
				await SendToAsync(
					destination,
					new DiscordEmbedBuilder()
						.WithTitle("Device pool health")
						.WithDescription("No outstanding usage to report.")
						.WithColor(SuccessColor)
						.WithTimestamp(DateTimeOffset.UtcNow),
					null,
					cancellationToken);
			}

			foreach (DevicePlatformReport platform in report.PlatformReports)
			{
				if (platform.DeviceReports.Count > 0)
				{
					await SendToAsync(destination, BuildDeviceProblemsEmbed(platform), null, cancellationToken);
				}
			}
		}

		/// <summary>
		/// Builds the health summary for one device pool, or null if nothing in it is worth reporting.
		/// </summary>
		/// <param name="pool">Pool to summarise.</param>
		/// <returns>An embed, or null when every platform in the pool is quiet.</returns>
		static DiscordEmbedBuilder? BuildPoolHealthEmbed(DevicePoolReport pool)
		{
			DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
				.WithTitle($"{Escape(pool.PoolName)} - device pool health")
				.WithTimestamp(DateTimeOffset.UtcNow);

			if (!String.IsNullOrEmpty(pool.PoolURL))
			{
				embed.WithUrl(pool.PoolURL);
			}

			int worst = 0;

			foreach (DevicePoolMetrics metrics in pool.Metrics)
			{
				DevicePoolHealth? health = RatePool(metrics);

				if (health == null)
				{
					continue;
				}

				if (health.Rank > worst)
				{
					worst = health.Rank;
				}

				embed.AddField($"{health.Marker} {Escape(metrics.PlatformName)}", DescribePoolMetrics(metrics));
			}

			if (embed.FieldCount == 0)
			{
				return null;
			}

			embed.WithColor(worst >= 3 ? FailureColor : worst >= 1 ? WarningColor : NeutralColor);

			return embed;
		}

		static string DescribePoolMetrics(DevicePoolMetrics metrics)
		{
			List<string> lines = new List<string>
			{
				$"Average load {metrics.AverageLoadPercentage}% across {metrics.Total} device(s) in {metrics.Streams.Count} stream(s)",
			};

			if (metrics.Disabled > 0 || metrics.Maintenance > 0)
			{
				lines.Add($"Unavailable: {metrics.Disabled} disabled, {metrics.Maintenance} in maintenance");
			}

			if (metrics.SaturationSpikes > 0)
			{
				lines.Add($"Saturation spikes: {metrics.SaturationSpikes}, averaging {metrics.SpikeDurationAverage:hh\\:mm} ({metrics.SpikeDurationPercentage}% of reservation time)");
			}

			if (metrics.Problems > 0)
			{
				lines.Add($"Problems: {metrics.Problems}, peaking at {metrics.MaxConcurrentProblems} at once ({metrics.MaxConcurrentProblemsPercentage}%)");
			}

			return String.Join("\n", lines);
		}

		static DiscordEmbedBuilder BuildDeviceProblemsEmbed(DevicePlatformReport platform)
		{
			DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
				.WithTitle($"{Escape(platform.PlatformName)} - device problems since the last report")
				.WithColor(FailureColor)
				.WithTimestamp(DateTimeOffset.UtcNow);

			// Worst first. The list is capped by the embed's field limit, so the order decides what survives.
			foreach (DeviceReport device in platform.DeviceReports.OrderByDescending(x => x.ProblemDelta))
			{
				bool cleaning = device.CleaningTime != null;

				List<string> lines = new List<string>
				{
					cleaning
						? $"Cleaning for {(int)device.CleaningTime!.Value.TotalHours} hour(s)"
						: $"{device.ProblemDelta} problem(s), a {device.ProblemPercent}% failure rate",
				};

				if (!String.IsNullOrEmpty(device.DeviceAddress))
				{
					lines.Add($"{Code(device.DeviceAddress)} in {Escape(device.PoolName)}");
				}

				// Links go in the value, never the name - Discord renders field names as plain text, so a markdown
				// link there arrives as its own source.
				if (!String.IsNullOrEmpty(device.DevicePoolURL))
				{
					lines.Add($"[View in pool]({device.DevicePoolURL})");
				}

				if (!String.IsNullOrEmpty(device.LastProblemURL))
				{
					lines.Add($"[{Escape(device.LastProblemDesc ?? "Last problem")}]({device.LastProblemURL})");
				}

				// Blue for a device that is merely busy being cleaned, red for one that is actually failing.
				embed.AddField($"{(cleaning ? "🔵" : "🔴")} {Escape(device.DeviceName)}", String.Join("\n", lines));
			}

			return embed;
		}

		/// <summary>
		/// Reports agents that are not making progress.
		/// </summary>
		/// <param name="report">Agents stuck conforming or upgrading.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public Task SendAgentReportAsync(AgentReport report, CancellationToken cancellationToken)
		{
			if (report.ConformLoop.Count == 0 && report.UpgradeLoop.Count == 0)
			{
				return Task.CompletedTask;
			}

			DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
				.WithTitle($"{_serverConfig.WarningPrefix}Agent status")
				.WithColor(WarningColor)
				.WithTimestamp(DateTimeOffset.UtcNow);

			// Both sections appear even when one is empty. "Conform issues: none" is information - it says the
			// upgrade problem is not a symptom of a conform problem - and it costs one line.
			AddAgentCounts(embed, "Conform issues", report.ConformLoop, count => $"has run conform {count} time(s)");
			AddAgentCounts(embed, "Upgrade issues", report.UpgradeLoop, count => $"has attempted to upgrade {count} time(s)");

			return SendAsync(_channels.ResolveCategory(DiscordChannelCategory.Agent), embed, null, cancellationToken);
		}

		/// <summary>
		/// Reports agents whose sessions have been conflicting.
		/// </summary>
		/// <remarks>
		/// Batched by Horde over the previous twelve hours, so this arrives already summarised - there is nothing to
		/// suppress and no state to compare against.
		/// </remarks>
		/// <param name="conflicts">Agents, and how many mismatches each accumulated.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public Task SendSessionConflictReportAsync(IReadOnlyList<(AgentId Id, int Count)> conflicts, CancellationToken cancellationToken)
		{
			if (conflicts.Count == 0)
			{
				return Task.CompletedTask;
			}

			DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
				.WithTitle($"{_serverConfig.WarningPrefix}Session conflicts")
				.WithDescription("Agents reporting session mismatches over the last 12 hours. Usually two agents sharing an identity, or one that has been cloned.")
				.WithColor(WarningColor)
				.WithTimestamp(DateTimeOffset.UtcNow);

			AddAgentCounts(embed, "Agents", conflicts, count => $"{count} mismatch(es)");

			return SendAsync(_channels.ResolveCategory(DiscordChannelCategory.Agent), embed, null, cancellationToken);
		}

		void AddAgentCounts(DiscordEmbedBuilder embed, string name, IReadOnlyList<(AgentId Id, int Count)> agents, Func<int, string> describe)
		{
			if (agents.Count == 0)
			{
				embed.AddField(name, "None.");
				return;
			}

			// Worst first, then by name so a tie does not reshuffle between reports and look like movement.
			IReadOnlyList<(AgentId Id, int Count)> ordered =
				[.. agents.OrderByDescending(x => x.Count).ThenBy(x => x.Id.ToString(), StringComparer.Ordinal)];

			embed.AddField(
				$"{name} ({agents.Count})",
				Summarise(ordered, x => $"[{Escape(x.Id.ToString())}]({GetAgentUrl(x.Id)}) {describe(x.Count)}"));
		}

		#endregion

		#region Test health

		/// <summary>
		/// Reports that a test's health has changed.
		/// </summary>
		/// <remarks>
		/// Horde decides when this is worth saying - it will not call for an unchanged report inside the reminder
		/// window - so the message is posted as it arrives. The one thing tracked here is whether a degradation was
		/// ever announced, because the recovery message only makes sense to a channel that heard about the problem.
		///
		/// Slack keeps a thread per test and hangs updates off it. That needs the message-state collection, which is
		/// Phase 4; until then each update is its own message.
		/// </remarks>
		/// <param name="report">Health of the test.</param>
		/// <param name="recipient">Channel the workflow nominated for its reports.</param>
		/// <param name="carbonCopies">User ids of the people who own or care about the test.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public async Task NotifyTestHealthReportAsync(ITestHealthReport report, string recipient, string[]? carbonCopies, CancellationToken cancellationToken)
		{
			DiscordDestination? destination = _channels.Resolve(recipient);

			if (destination == null)
			{
				return;
			}

			// Keyed on the test rather than on the report document, which is what the Slack sink uses. A report is
			// created or updated per test per stream, so this identifies the same thing - and it stays stable if a
			// fresh document is ever written for a test, which is exactly when the recovery message must still pair
			// up with the degradation that preceded it. It also keeps MongoDB's ObjectId out of the plugin.
			string eventId = $"test-health-{report.StreamId}-{report.TestId}";
			string stream = GetStreamConfig(report.StreamId)?.Name ?? report.StreamId.ToString();

			DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
				.WithTitle($"{(report.IsHealthy ? String.Empty : _serverConfig.ErrorPrefix)}{Escape(report.TestName)}")
				.WithUrl(GetTestHealthUrl(report).ToString())
				.WithTimestamp(report.LastUpdateDateUtc)
				.AddField("Stream", Escape(stream), true);

			if (report.IsHealthy)
			{
				if (!_repeats.Clear(eventId))
				{
					return;
				}

				embed.WithDescription($"Test health has recovered to **{Escape(report.State)}**.")
					.WithColor(SuccessColor);
			}
			else
			{
				_repeats.Record(eventId);

				embed.WithDescription(DescribeTestHealth(report))
					.WithColor(FailureColor)
					.AddField("State", Escape(report.State), true);

				AddTestRates(embed, report);
			}

			await SendToAsync(destination, embed, await GetUsersAsync(carbonCopies, cancellationToken), cancellationToken);
		}

		static string DescribeTestHealth(ITestHealthReport report)
		{
			if (report.PreviousState != null && report.PreviousState != report.State)
			{
				return $"Test health has changed from **{Escape(report.PreviousState)}** to **{Escape(report.State)}**.";
			}

			int days = (int)(DateTime.UtcNow - report.LastUpdateDateUtc).TotalDays;

			return days >= 1
				? $"Test health has been **{Escape(report.State)}** for {days} day(s) with no improvement. Consider disabling the test or placing it under audit."
				: $"Test health has degraded to **{Escape(report.State)}**.";
		}

		static void AddTestRates(DiscordEmbedBuilder embed, ITestHealthReport report)
		{
			embed.AddField("Success rate", $"{report.SuccessRate}%", true);
			embed.AddField("Failure rate", $"{report.FailureRate}%", true);

			// Only shown when non-zero: a wall of zeroes buries the one rate that is not.
			if (report.CatastrophicFailureRate > 0)
			{
				embed.AddField("Catastrophic failures", $"{report.CatastrophicFailureRate}%", true);
			}

			if (report.RedundantErrorRate > 0)
			{
				embed.AddField("Redundant errors", $"{report.RedundantErrorRate}%", true);
			}
		}

		/// <summary>
		/// Turns the user ids carried on a report into users.
		/// </summary>
		/// <remarks>
		/// Everything that fails here fails quietly - an id that will not parse, a user who has since been deleted,
		/// or a lookup that throws. None of those are worth losing the notification over, and the report is about a
		/// test rather than about the people copied on it.
		/// </remarks>
		/// <param name="userIds">User ids, as Horde stores them on the report.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>The users that could be resolved.</returns>
		async Task<IReadOnlyList<IUser>?> GetUsersAsync(string[]? userIds, CancellationToken cancellationToken)
		{
			if (userIds == null || userIds.Length == 0)
			{
				return null;
			}

			List<IUser> users = new List<IUser>();

			foreach (string userId in userIds)
			{
				if (UserId.TryParse(userId, out UserId parsed) && await GetUserAsync(parsed, cancellationToken) is IUser user)
				{
					users.Add(user);
				}
			}

			return users;
		}

		/// <summary>
		/// Looks up a Horde user, treating every failure as "not found".
		/// </summary>
		/// <param name="userId">User to look up.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>The user, or null.</returns>
		async Task<IUser?> GetUserAsync(UserId userId, CancellationToken cancellationToken)
		{
			try
			{
				return await _hordeUsers.GetUserAsync(userId, cancellationToken);
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Could not look up Horde user {UserId} while addressing a Discord notification.", userId);
				return null;
			}
		}

		#endregion

		#region Links

		/// <summary>
		/// Base of a Discord deep link. The client intercepts these; a browser follows them to the web app.
		/// </summary>
		const string ChannelLinkPrefix = "https://discord.com/channels";

		/// <summary>
		/// Whether this sink should answer the dashboard's requests for chat deep links.
		/// </summary>
		/// <remarks>
		/// **Horde takes the first non-null answer from any sink and ignores the rest**
		/// (<c>NotificationService.GetDirectMessageLinkAsync</c>), and sink order is registration order, which a
		/// plugin does not control. Answering unconditionally would therefore be a coin toss over whether the
		/// dashboard's "message these people" button opens Discord or Slack - and silently changing where an existing
		/// Slack deployment's buttons go is exactly what a plugin that "runs alongside Slack" must not do.
		///
		/// So the default is to answer only when nobody else will: unset means links are provided when the Build
		/// plugin has no Slack token configured. Setting it explicitly overrides that in either direction.
		/// </remarks>
		public bool ProvidesDeepLinks
			=> _serverConfig.IsConfigured
				&& (_serverConfig.EnableDeepLinks ?? String.IsNullOrEmpty(_buildServerConfig.SlackToken));

		/// <summary>
		/// Builds a link that opens a conversation with somebody.
		/// </summary>
		/// <remarks>
		/// **One recipient only.** Slack supports up to eight in a multi-person DM; Discord's group DMs are limited
		/// to user accounts and OAuth flows a bot has no access to, so there is no honest answer for more than one
		/// person and null is better than a link to the wrong conversation.
		/// </remarks>
		/// <param name="userIds">Horde users to open a conversation with.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>A link, or null if one cannot be built.</returns>
		public async Task<string?> GetDirectMessageLinkAsync(IReadOnlyList<UserId> userIds, CancellationToken cancellationToken)
		{
			if (!ProvidesDeepLinks || userIds.Count != 1)
			{
				return null;
			}

			IUser? user = await GetUserAsync(userIds[0], cancellationToken);

			if (user == null)
			{
				return null;
			}

			string? discordUserId = await _discordUsers.GetUserIdAsync(user, cancellationToken);

			if (discordUserId == null)
			{
				return null;
			}

			string? channelId = await _client.GetDirectMessageChannelAsync(discordUserId, cancellationToken);

			// A DM is addressed as a channel under the pseudo-guild '@me'.
			return channelId == null ? null : $"{ChannelLinkPrefix}/@me/{channelId}";
		}

		/// <summary>
		/// Builds a link that opens one of Horde's channels in Discord.
		/// </summary>
		/// <remarks>
		/// Only for a channel the map names explicitly. Sending someone to the catch-all channel would be a link that
		/// works and is wrong, which is worse than no link at all - and a guild id is required, because Discord
		/// addresses a channel by guild and channel together.
		/// </remarks>
		/// <param name="channel">Horde channel id, as a workflow or report carries it.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>A link, or null if the channel is not mapped.</returns>
		public Task<string?> GetChannelLinkAsync(string channel, CancellationToken cancellationToken)
		{
			if (!ProvidesDeepLinks || !_channels.IsMapped(channel))
			{
				return Task.FromResult<string?>(null);
			}

			DiscordDestination? destination = _channels.Resolve(channel);

			return Task.FromResult(destination?.GuildId == null
				? null
				: $"{ChannelLinkPrefix}/{destination.GuildId}/{destination.ChannelId}");
		}

		#endregion

		#region Sending

		/// <summary>
		/// Posts an embed to a set of resolved destinations.
		/// </summary>
		/// <remarks>
		/// The single exit point for everything this class sends, which is what keeps the configured-or-not gate and
		/// the fallback-channel note in one place instead of at every call site.
		/// </remarks>
		/// <param name="destinations">Where to post.</param>
		/// <param name="embed">What to post.</param>
		/// <param name="forUsers">Users the notification was aimed at, named in plain text.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public Task SendAsync(IReadOnlyList<DiscordDestination> destinations, DiscordEmbedBuilder embed, IEnumerable<IUser>? forUsers, CancellationToken cancellationToken)
			=> SendAsync(destinations, embed, forUsers, null, cancellationToken);

		/// <summary>
		/// Posts an embed, with buttons, to a set of resolved destinations.
		/// </summary>
		/// <remarks>
		/// Only issue triage passes components. Everything else describes something that has already happened and
		/// has nothing to offer the reader but a link.
		/// </remarks>
		/// <param name="destinations">Where to post.</param>
		/// <param name="embed">What to post.</param>
		/// <param name="forUsers">Users the notification was aimed at, named in plain text.</param>
		/// <param name="components">Buttons to attach, or null for none.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public async Task SendAsync(IReadOnlyList<DiscordDestination> destinations, DiscordEmbedBuilder embed, IEnumerable<IUser>? forUsers, DiscordComponentBuilder? components, CancellationToken cancellationToken)
		{
			if (!_serverConfig.IsConfigured || destinations.Count == 0)
			{
				return;
			}

			DiscordAddressee addressee = await DescribeAsync(forUsers, cancellationToken);
			DiscordEmbed built = embed.Build();

			foreach (DiscordDestination destination in destinations)
			{
				DiscordMessageBuilder message = new DiscordMessageBuilder().AddEmbed(built);

				if (components != null && !components.IsEmpty)
				{
					message.WithComponents(components);
				}

				// A message in the catch-all says which Horde channel it was meant for. Without that the channel
				// fills up with notifications nobody can trace back to a missing mapping.
				string? note = destination.IsFallback && destination.SourceChannel != null
					? $"Unmapped Horde channel {Code(destination.SourceChannel)}"
					: null;

				string content = String.Join(" · ", new[] { addressee.Text, note }.Where(x => x != null));

				if (content.Length > 0)
				{
					message.WithContent(content);
				}

				// Only the people this notification is actually about may be pinged. Everything else - a step name, a
				// commit description, an error line - is reproduced from somewhere else and must stay inert.
				if (addressee.MentionedUserIds.Count > 0)
				{
					message.WithAllowedMentions(DiscordAllowedMentions.ForUsers(addressee.MentionedUserIds));
				}

				await _client.CreateMessageAsync(destination.ChannelId, message.Build(), cancellationToken);
			}
		}

		/// <summary>
		/// Posts an embed to a single destination, for the notifications that carry their own channel.
		/// </summary>
		/// <param name="destination">Where to post. Nothing is sent when this is null.</param>
		/// <param name="embed">What to post.</param>
		/// <param name="forUsers">Users the notification is about, mentioned where they are known.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public Task SendToAsync(DiscordDestination? destination, DiscordEmbedBuilder embed, IEnumerable<IUser>? forUsers, CancellationToken cancellationToken)
			=> destination == null
				? Task.CompletedTask
				: SendAsync(new[] { destination }, embed, forUsers, cancellationToken);

		/// <summary>
		/// Sends a notification to each person it is addressed to, falling back to a channel for anyone unreachable.
		/// </summary>
		/// <remarks>
		/// The delivery rule for everything aimed at a person rather than at a team. A direct message is the right
		/// shape for a subscription - it is about one person's build, and putting it in a shared channel per
		/// subscriber is what makes a job channel unusable - but a Discord bot cannot DM everyone. It needs the user
		/// in the map, it needs to share a guild with them, and they have to accept messages from server members.
		///
		/// So a miss degrades rather than disappearing: everyone who could not be reached is named once in the
		/// fallback channel, mentioned if they are mapped and in plain text if not. **A notification is never
		/// dropped for want of a mapping**, which is what makes the map safe to fill in gradually.
		///
		/// With nobody to address, this posts to the fallback - the sensible reading for a notification that came
		/// with no user at all. Callers that should stay silent in that case check first.
		/// </remarks>
		/// <param name="users">People the notification is for.</param>
		/// <param name="fallback">Where to post for anyone who could not be reached.</param>
		/// <param name="embed">What to send.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public Task SendToUsersAsync(IEnumerable<IUser>? users, IReadOnlyList<DiscordDestination> fallback, DiscordEmbedBuilder embed, CancellationToken cancellationToken)
			=> SendToUsersAsync(users, fallback, embed, null, cancellationToken);

		/// <summary>
		/// Sends a notification with buttons to each person it is addressed to, falling back to a channel.
		/// </summary>
		/// <remarks>
		/// The buttons go on both copies. A triage action is the same action wherever it is taken from, and the
		/// custom id carries everything needed to identify it - see <see cref="DiscordCustomId"/>.
		/// </remarks>
		/// <param name="users">People the notification is for.</param>
		/// <param name="fallback">Where to post for anyone who could not be reached.</param>
		/// <param name="embed">What to send.</param>
		/// <param name="components">Buttons to attach, or null for none.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public async Task SendToUsersAsync(IEnumerable<IUser>? users, IReadOnlyList<DiscordDestination> fallback, DiscordEmbedBuilder embed, DiscordComponentBuilder? components, CancellationToken cancellationToken)
		{
			if (!_serverConfig.IsConfigured)
			{
				return;
			}

			IReadOnlyList<IUser> recipients = Distinct(users);

			if (recipients.Count == 0)
			{
				await SendAsync(fallback, embed, null, components, cancellationToken);
				return;
			}

			DiscordEmbed built = embed.Build();
			List<IUser> unreachable = new List<IUser>();

			foreach (IUser user in recipients)
			{
				if (!await TrySendDirectAsync(user, built, components, cancellationToken))
				{
					unreachable.Add(user);
				}
			}

			if (unreachable.Count > 0)
			{
				await SendAsync(fallback, embed, unreachable, components, cancellationToken);
			}
		}

		/// <summary>
		/// Tries to send an embed to somebody as a direct message.
		/// </summary>
		/// <remarks>
		/// Reports failure rather than throwing, because every reason this fails is a normal state of the world:
		/// nobody has mapped this person yet, the bot shares no guild with them, or they do not accept direct
		/// messages. The caller decides what to do instead.
		/// </remarks>
		/// <param name="user">Person to message.</param>
		/// <param name="embed">What to send.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>True if the message was delivered.</returns>
		public Task<bool> TrySendDirectAsync(IUser user, DiscordEmbed embed, CancellationToken cancellationToken)
			=> TrySendDirectAsync(user, embed, null, cancellationToken);

		/// <summary>
		/// Tries to send an embed with buttons to somebody as a direct message.
		/// </summary>
		/// <param name="user">Person to message.</param>
		/// <param name="embed">What to send.</param>
		/// <param name="components">Buttons to attach, or null for none.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>True if the message was delivered.</returns>
		public async Task<bool> TrySendDirectAsync(IUser user, DiscordEmbed embed, DiscordComponentBuilder? components, CancellationToken cancellationToken)
		{
			if (!_serverConfig.IsConfigured)
			{
				return false;
			}

			string? userId = await _discordUsers.GetUserIdAsync(user, cancellationToken);

			if (userId == null)
			{
				return false;
			}

			string? channelId = await _client.GetDirectMessageChannelAsync(userId, cancellationToken);

			if (channelId == null)
			{
				return false;
			}

			DiscordMessageBuilder builder = new DiscordMessageBuilder().AddEmbed(embed);

			if (components != null && !components.IsEmpty)
			{
				builder.WithComponents(components);
			}

			return await _client.CreateMessageAsync(channelId, builder.Build(), cancellationToken) != null;
		}

		/// <summary>
		/// Works out how to address a set of people in a channel message.
		/// </summary>
		/// <remarks>
		/// Mentioned where the map knows them, named in plain text where it does not. The plain-text half is not a
		/// placeholder for something better - it is what keeps a half-filled map from costing anyone a notification.
		///
		/// <c>cc</c> rather than a bare mention line, which is the more usual Discord shape, precisely because of
		/// that plain-text half: a lone "Ada Lovelace" above an embed reads as a caption rather than an addressee.
		/// It is also honest about what the line means - the notification went to the channel, and these are the
		/// people who should see it, not the only people who will.
		/// </remarks>
		/// <param name="users">People the notification is about.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>The addressee line, and the ids that may be pinged.</returns>
		async Task<DiscordAddressee> DescribeAsync(IEnumerable<IUser>? users, CancellationToken cancellationToken)
		{
			IReadOnlyList<IUser> recipients = Distinct(users);

			if (recipients.Count == 0)
			{
				return DiscordAddressee.None;
			}

			List<string> parts = new List<string>();
			List<string> mentioned = new List<string>();

			foreach (IUser user in recipients)
			{
				string? userId = await _discordUsers.GetUserIdAsync(user, cancellationToken);

				if (userId == null)
				{
					parts.Add(Escape(user.Name));
				}
				else
				{
					parts.Add($"<@{userId}>");
					mentioned.Add(userId);
				}
			}

			return new DiscordAddressee($"cc {String.Join(", ", parts)}", mentioned);
		}

		/// <summary>
		/// How a message names the people it is for.
		/// </summary>
		/// <param name="Text">Line to put above the embed, or null when it is addressed to nobody.</param>
		/// <param name="MentionedUserIds">Ids allowed to be pinged, which must be listed explicitly.</param>
		sealed record DiscordAddressee(string? Text, IReadOnlyList<string> MentionedUserIds)
		{
			/// <summary>Addressed to nobody.</summary>
			public static DiscordAddressee None { get; } = new DiscordAddressee(null, Array.Empty<string>());
		}

		#endregion

		#region Formatting

		/// <summary>
		/// How a device pool platform is faring, if it is worth mentioning at all.
		/// </summary>
		/// <param name="Rank">Severity, for picking the colour of a pool that mixes several.</param>
		/// <param name="Marker">Emoji shown against the platform.</param>
		sealed record DevicePoolHealth(int Rank, string Marker);

		/// <summary>Average load at which a platform is treated as saturated.</summary>
		/// <remarks>
		/// These four thresholds are the same ones the Slack sink uses. Deliberately: both sinks run against the same
		/// farm, and a platform that is red in one channel and orange in the other is worse than either being wrong.
		/// </remarks>
		const int HighLoadPercent = 40;

		/// <summary>Concurrent problem rate at which a platform is treated as saturated.</summary>
		const int HighProblemPercent = 50;

		/// <summary>Average load at which a platform is worth watching.</summary>
		const int ElevatedLoadPercent = 20;

		/// <summary>Concurrent problem rate at which a platform is worth watching.</summary>
		const int ElevatedProblemPercent = 30;

		static DevicePoolHealth? RatePool(DevicePoolMetrics metrics)
		{
			if (metrics.AverageLoadPercentage >= HighLoadPercent || metrics.MaxConcurrentProblemsPercentage >= HighProblemPercent)
			{
				return new DevicePoolHealth(3, "🔴");
			}

			if (metrics.AverageLoadPercentage >= ElevatedLoadPercent || metrics.MaxConcurrentProblemsPercentage >= ElevatedProblemPercent)
			{
				return new DevicePoolHealth(2, "🟠");
			}

			// A pool with nothing left in it is not busy, it is gone - which is worth saying, and would otherwise
			// look identical to a healthy one.
			if (metrics.Total > 0 && metrics.Total == metrics.Disabled + metrics.Maintenance)
			{
				return new DevicePoolHealth(1, "⚫");
			}

			return metrics.Problems == 0 && metrics.SaturationSpikes == 0 ? null : new DevicePoolHealth(1, "🟡");
		}

		/// <summary>
		/// Finds a stream's configuration, which carries its name and notification channel.
		/// </summary>
		/// <remarks>
		/// Null when the global config has not loaded or the stream has since been removed. Both are survivable -
		/// callers fall back to the id - so this reports at debug rather than throwing.
		/// </remarks>
		/// <param name="streamId">Stream to look up.</param>
		/// <returns>The stream's configuration, or null.</returns>
		StreamConfig? GetStreamConfig(StreamId streamId)
		{
			try
			{
				if (_buildConfig.CurrentValue.TryGetStream(streamId, out StreamConfig? streamConfig))
				{
					return streamConfig;
				}

				_logger.LogDebug("No stream configuration for {StreamId}; falling back to the stream id.", streamId);
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Build configuration is not available; falling back to the stream id.");
			}

			return null;
		}

		static IEnumerable<IUser>? Only(IUser? user) => user == null ? null : new[] { user };

		static bool HasAny(IEnumerable<IUser>? users) => users != null && users.Any();

		/// <summary>
		/// Removes repeats from a set of recipients.
		/// </summary>
		/// <remarks>
		/// Horde can name the same person twice - subscribed to a step and its label, say - and a duplicate here
		/// costs them two identical direct messages rather than one.
		/// </remarks>
		/// <param name="users">Recipients, possibly with repeats or nulls.</param>
		/// <returns>Each distinct recipient once.</returns>
		static IReadOnlyList<IUser> Distinct(IEnumerable<IUser>? users)
			=> users == null ? Array.Empty<IUser>() : [.. users.Where(x => x != null).DistinctBy(x => x.Id)];

		Uri GetJobUrl(JobId jobId) => new Uri(_serverInfo.DashboardUrl, $"job/{jobId}");

		Uri GetStepUrl(JobId jobId, JobStepId stepId) => new Uri(_serverInfo.DashboardUrl, $"job/{jobId}?step={stepId}");

		Uri GetAgentUrl(AgentId agentId) => new Uri(_serverInfo.DashboardUrl, $"agents?agentId={agentId}");

		Uri GetTestHealthUrl(ITestHealthReport report)
			=> new Uri(_serverInfo.DashboardUrl, $"test-automation?stream={report.StreamId}&view=1&health={report.TestId}");

		/// <summary>
		/// Renders a list, stopping after a few and saying how many were left.
		/// </summary>
		/// <remarks>
		/// Every list Horde hands the sink is unbounded - failing steps, waiting jobs, broken agents - and the cut has
		/// to be visible. A list that silently ends at ten reads as "ten things are wrong" rather than "at least ten".
		/// </remarks>
		/// <typeparam name="T">Type of item being listed.</typeparam>
		/// <param name="items">Items to list.</param>
		/// <param name="format">Renders one item as a line.</param>
		/// <param name="max">How many to list before summarising.</param>
		/// <param name="more">Added after the count, when there is somewhere better to look.</param>
		/// <returns>The rendered list.</returns>
		static string Summarise<T>(IReadOnlyList<T> items, Func<T, string> format, int max = MaxListedItems, string? more = null)
		{
			string value = String.Join("\n", items.Take(max).Select(format));

			if (items.Count > max)
			{
				value += $"\nand {items.Count - max} more";

				if (more != null)
				{
					value += $" - {more}";
				}
			}

			return value;
		}

		/// <summary>
		/// Wraps text in a code fence, sized so it survives being put in an embed field.
		/// </summary>
		/// <remarks>
		/// The fence has to be applied here rather than left to the field, because the builder truncates a field value
		/// at 1024 characters and would take the closing fence with it - leaving Discord rendering the rest of the
		/// message as code. Any fence inside the text is neutralised for the same reason.
		/// </remarks>
		/// <param name="text">Text to quote.</param>
		/// <returns>A fenced block that fits in a field value.</returns>
		static string CodeBlock(string text)
		{
			const int FenceLength = 8;

			string quoted = text.Replace("```", "'''", StringComparison.Ordinal);

			return $"```\n{DiscordEmbedLimits.Truncate(quoted, DiscordEmbedLimits.FieldValue - FenceLength)}\n```";
		}

		/// <summary>
		/// Wraps text in an inline code span.
		/// </summary>
		/// <remarks>
		/// Deliberately *not* escaped, which is the opposite of the rule everywhere else. Discord renders a code span
		/// literally, so backslash-escaping markdown inside one does not protect anything - it just puts the
		/// backslashes on screen, and file paths and channel ids are full of underscores. A backtick is the only
		/// character that matters here, because one in the text would close the span early and let the rest out.
		/// </remarks>
		/// <param name="text">Text to render as code.</param>
		/// <returns>An inline code span.</returns>
		static string Code(string text) => $"`{text.Replace('`', '\'')}`";

		string Prefix(LabelOutcome outcome) => outcome switch
		{
			LabelOutcome.Failure => _serverConfig.ErrorPrefix,
			LabelOutcome.Warnings => _serverConfig.WarningPrefix,
			_ => String.Empty,
		};

		string Prefix(JobStepOutcome outcome) => outcome switch
		{
			JobStepOutcome.Failure => _serverConfig.ErrorPrefix,
			JobStepOutcome.Warnings => _serverConfig.WarningPrefix,
			_ => String.Empty,
		};

		static int GetColor(LabelOutcome outcome) => outcome switch
		{
			LabelOutcome.Success => SuccessColor,
			LabelOutcome.Warnings => WarningColor,
			LabelOutcome.Failure => FailureColor,
			_ => NeutralColor,
		};

		static int GetColor(JobStepOutcome outcome) => outcome switch
		{
			JobStepOutcome.Success => SuccessColor,
			JobStepOutcome.Warnings => WarningColor,
			JobStepOutcome.Failure => FailureColor,
			_ => NeutralColor,
		};

		static string Describe(LabelOutcome outcome) => outcome switch
		{
			LabelOutcome.Success => "Success",
			LabelOutcome.Warnings => "Completed with warnings",
			LabelOutcome.Failure => "Failed",
			_ => "Unknown",
		};

		static string Describe(JobStepOutcome outcome) => outcome switch
		{
			JobStepOutcome.Success => "Success",
			JobStepOutcome.Warnings => "Completed with warnings",
			JobStepOutcome.Failure => "Failed",
			_ => "Unknown",
		};

		static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1.0
			? $"{(int)duration.TotalHours}h {duration.Minutes}m"
			: duration.TotalMinutes >= 1.0
				? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
				: $"{duration.Seconds}s";

		static string FirstLine(string message)
		{
			int end = message.IndexOfAny(['\r', '\n']);
			return end < 0 ? message : message[..end];
		}

		static string Escape(string text) => DiscordMarkdown.Escape(text);

		#endregion
	}
}
