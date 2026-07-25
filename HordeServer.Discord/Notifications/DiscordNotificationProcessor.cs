// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using EpicGames.Horde.Jobs;
using EpicGames.Horde.Jobs.Graphs;
using EpicGames.Horde.Logs;
using HordeServer.Discord.Client;
using HordeServer.Logs;
using HordeServer.Notifications;
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
	/// Experimental plugin splits its Slack sink from its processor.
	///
	/// Phase 1 covers job and step outcomes, broadcast to the channels named in server configuration. Notifications
	/// aimed at a specific person still go to the channel, with that person named in plain text - Discord needs a
	/// hand-maintained email-to-snowflake map before it can DM or mention anyone, which is Phase 3. Posting them
	/// unaddressed is the honest interim: the information arrives, it just is not routed yet.
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
		readonly DiscordServerConfig _serverConfig;
		readonly IServerInfo _serverInfo;
		readonly ILogger _logger;
		readonly IReadOnlyList<string> _jobChannels;

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="client">Client used to post.</param>
		/// <param name="serverConfig">Server configuration, for channel routing and emoji prefixes.</param>
		/// <param name="serverInfo">Server information, for dashboard links.</param>
		/// <param name="logger">Logger for delivery problems.</param>
		public DiscordNotificationProcessor(DiscordClient client, IOptions<DiscordServerConfig> serverConfig, IServerInfo serverInfo, ILogger<DiscordNotificationProcessor> logger)
		{
			_client = client;
			_serverConfig = serverConfig.Value;
			_serverInfo = serverInfo;
			_logger = logger;
			_jobChannels = DiscordChannelList.Parse(_serverConfig.JobNotificationChannel, "JobNotificationChannel", logger);
		}

		/// <summary>
		/// Whether there is both a way to send job notifications and somewhere to send them.
		/// </summary>
		/// <remarks>
		/// The plugin registers its sink whether or not it is configured, so this is the real gate. Running it
		/// unconfigured is a supported way to verify the plugin loads before any Discord credentials exist.
		/// </remarks>
		public bool CanSendJobNotifications => _serverConfig.IsConfigured && _jobChannels.Count > 0;

		/// <summary>
		/// Reports that a job finished.
		/// </summary>
		/// <param name="job">Job that finished.</param>
		/// <param name="outcome">How it went.</param>
		/// <param name="forUser">User the notification was aimed at, if it was aimed at anyone.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public Task NotifyJobCompleteAsync(IJob job, LabelOutcome outcome, IUser? forUser, CancellationToken cancellationToken)
		{
			DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
				.WithTitle($"{Prefix(outcome)}{job.Name}")
				.WithUrl(GetJobUrl(job.Id).ToString())
				.WithColor(GetColor(outcome))
				.WithTimestamp(job.UpdateTimeUtc);

			AddJobContext(embed, job);
			embed.AddField("Outcome", Describe(outcome), true);

			return SendToJobChannelsAsync(embed, Only(forUser), cancellationToken);
		}

		/// <summary>
		/// Reports that a job step finished.
		/// </summary>
		/// <param name="job">Job containing the step.</param>
		/// <param name="step">Step that finished.</param>
		/// <param name="node">Node the step ran.</param>
		/// <param name="events">Log events produced by the step.</param>
		/// <param name="usersToNotify">Users the notification was aimed at.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public Task NotifyJobStepCompleteAsync(IJob job, IJobStep step, INode node, IReadOnlyList<ILogEventData> events, IEnumerable<IUser>? usersToNotify, CancellationToken cancellationToken)
			=> SendStepMessageAsync(job, step, node, events, usersToNotify, GetColor(step.Outcome), Prefix(step.Outcome), Describe(step.Outcome), cancellationToken);

		/// <summary>
		/// Reports that a job step was aborted.
		/// </summary>
		/// <param name="job">Job containing the step.</param>
		/// <param name="step">Step that was aborted.</param>
		/// <param name="node">Node the step was running.</param>
		/// <param name="events">Log events produced before the abort.</param>
		/// <param name="usersToNotify">Users the notification was aimed at.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public Task NotifyJobStepAbortedAsync(IJob job, IJobStep step, INode node, IReadOnlyList<ILogEventData> events, IEnumerable<IUser>? usersToNotify, CancellationToken cancellationToken)
		{
			// An abort is not a failure - somebody chose it - so it gets the neutral colour and says who, when Horde
			// knows. The cancellation reason is the part people actually want and is easy to miss in the dashboard.
			string reason = step.CancellationReason ?? job.CancellationReason ?? "Aborted";

			return SendStepMessageAsync(job, step, node, events, usersToNotify, NeutralColor, _serverConfig.WarningPrefix, reason, cancellationToken);
		}

		/// <summary>
		/// Reports that a label finished.
		/// </summary>
		/// <param name="job">Job the label belongs to.</param>
		/// <param name="label">Label that finished.</param>
		/// <param name="outcome">How it went.</param>
		/// <param name="stepData">Name, outcome and link for each step in the label.</param>
		/// <param name="forUser">User the notification was aimed at.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public Task NotifyLabelCompleteAsync(IJob job, ILabel label, LabelOutcome outcome, IReadOnlyList<(string Name, JobStepOutcome Outcome, Uri Url)> stepData, IUser? forUser, CancellationToken cancellationToken)
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
				embed.AddField($"Steps ({notable.Count})", FormatStepList(notable));
			}

			return SendToJobChannelsAsync(embed, Only(forUser), cancellationToken);
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
				IEnumerable<string> jobs = pool
					.Take(MaxListedItems)
					.Select(x => $"[{Escape(x.JobName)}]({GetJobUrl(JobId.Parse(x.JobId))})");

				string value = String.Join("\n", jobs);

				if (pool.Count() > MaxListedItems)
				{
					value += $"\nand {pool.Count() - MaxListedItems} more";
				}

				embed.AddField($"{pool.Key} ({pool.Count()})", value);
			}

			return SendToJobChannelsAsync(embed, null, cancellationToken);
		}

		Task SendStepMessageAsync(IJob job, IJobStep step, INode node, IReadOnlyList<ILogEventData> events, IEnumerable<IUser>? usersToNotify, int color, string prefix, string outcome, CancellationToken cancellationToken)
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

			return SendToJobChannelsAsync(embed, usersToNotify, cancellationToken);
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

			IEnumerable<string> lines = notable
				.Take(MaxQuotedLogEvents)
				.Select(x => $"{(x.Severity == LogEventSeverity.Error ? "✘" : "⚠")} {Escape(FirstLine(x.Message))}");

			string value = String.Join("\n", lines);

			if (notable.Count > MaxQuotedLogEvents)
			{
				value += $"\nand {notable.Count - MaxQuotedLogEvents} more - see the log for the rest";
			}

			embed.AddField($"Events ({notable.Count})", value);
		}

		string FormatStepList(IReadOnlyList<(string Name, JobStepOutcome Outcome, Uri Url)> steps)
		{
			IEnumerable<string> lines = steps
				.Take(MaxListedItems)
				.Select(x => $"{Prefix(x.Outcome).TrimEnd()} [{Escape(x.Name)}]({x.Url})".TrimStart());

			string value = String.Join("\n", lines);

			if (steps.Count > MaxListedItems)
			{
				value += $"\nand {steps.Count - MaxListedItems} more";
			}

			return value;
		}

		static IEnumerable<IUser>? Only(IUser? user) => user == null ? null : new[] { user };

		async Task SendToJobChannelsAsync(DiscordEmbedBuilder embed, IEnumerable<IUser>? forUsers, CancellationToken cancellationToken)
		{
			if (!CanSendJobNotifications)
			{
				return;
			}

			DiscordMessageBuilder message = new DiscordMessageBuilder().AddEmbed(embed);

			// Named in plain text rather than mentioned. Until the Phase 3 user map exists there is no snowflake to
			// mention with, and a notification that arrives addressed to nobody beats one that does not arrive.
			IReadOnlyList<string> names = forUsers == null
				? Array.Empty<string>()
				: [.. forUsers.Select(x => x.Name).Where(x => !String.IsNullOrEmpty(x)).Distinct()];

			if (names.Count > 0)
			{
				message.WithContent($"For {String.Join(", ", names.Select(Escape))}");
			}

			DiscordMessage payload = message.Build();

			foreach (string channelId in _jobChannels)
			{
				await _client.CreateMessageAsync(channelId, payload, cancellationToken);
			}
		}

		Uri GetJobUrl(JobId jobId) => new Uri(_serverInfo.DashboardUrl, $"job/{jobId}");

		Uri GetStepUrl(JobId jobId, JobStepId stepId) => new Uri(_serverInfo.DashboardUrl, $"job/{jobId}?step={stepId}");

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
	}
}
