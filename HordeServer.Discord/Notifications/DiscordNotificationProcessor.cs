// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Text.Json;
using EpicGames.Horde.Agents;
using EpicGames.Horde.Jobs;
using EpicGames.Horde.Jobs.Graphs;
using EpicGames.Horde.Logs;
using EpicGames.Horde.Streams;
using EpicGames.Horde.Users;
using HordeServer.Agents;
using HordeServer.Configuration;
using HordeServer.Devices;
using HordeServer.Discord.Client;
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
		readonly DiscordRepeatFilter _repeats;
		readonly DiscordServerConfig _serverConfig;
		readonly IOptionsMonitor<BuildConfig> _buildConfig;
		readonly IUserCollection _users;
		readonly IServerInfo _serverInfo;
		readonly ILogger _logger;

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="client">Client used to post.</param>
		/// <param name="channels">Works out where each notification goes.</param>
		/// <param name="repeats">Suppresses re-announcing a condition that has not changed.</param>
		/// <param name="serverConfig">Server configuration, for the bot token and emoji prefixes.</param>
		/// <param name="buildConfig">Build plugin global configuration, for per-stream notification channels.</param>
		/// <param name="users">User lookup, for turning the user ids on a report into names.</param>
		/// <param name="serverInfo">Server information, for dashboard links.</param>
		/// <param name="logger">Logger for delivery problems.</param>
		public DiscordNotificationProcessor(DiscordClient client, DiscordChannelResolver channels, DiscordRepeatFilter repeats, IOptions<DiscordServerConfig> serverConfig, IOptionsMonitor<BuildConfig> buildConfig, IUserCollection users, IServerInfo serverInfo, ILogger<DiscordNotificationProcessor> logger)
		{
			_client = client;
			_channels = channels;
			_repeats = repeats;
			_serverConfig = serverConfig.Value;
			_buildConfig = buildConfig;
			_users = users;
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

			// Routed by the job and its stream rather than the base category, which is what Horde itself does for
			// completions - and the only path that honours a per-template or per-stream notification channel.
			return SendAsync(
				_channels.ResolveJobCompletion(job, GetStreamConfig(job.StreamId), outcome),
				embed,
				Only(forUser),
				cancellationToken);
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
				embed.AddField($"Steps ({notable.Count})", Summarise(notable, x => $"{Prefix(x.Outcome).TrimEnd()} [{Escape(x.Name)}]({x.Url})".TrimStart()));
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
				IReadOnlyList<JobScheduledNotification> jobs = [.. pool];

				embed.AddField(
					$"{pool.Key} ({jobs.Count})",
					Summarise(jobs, x => $"[{Escape(x.JobName)}]({GetJobUrl(JobId.Parse(x.JobId))})"));
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

			embed.AddField(
				$"Events ({notable.Count})",
				Summarise(
					notable,
					x => $"{(x.Severity == LogEventSeverity.Error ? "✘" : "⚠")} {Escape(FirstLine(x.Message))}",
					MaxQuotedLogEvents,
					"see the log for the rest"));
		}

		Task SendToJobChannelsAsync(DiscordEmbedBuilder embed, IEnumerable<IUser>? forUsers, CancellationToken cancellationToken)
			=> SendAsync(_channels.ResolveCategory(DiscordChannelCategory.Job), embed, forUsers, cancellationToken);

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
		/// </remarks>
		/// <param name="errorMessage">What went wrong.</param>
		/// <param name="fileName">File that could not be read.</param>
		/// <param name="change">Commit that probably caused it.</param>
		/// <param name="author">Author of that commit.</param>
		/// <param name="description">Description of that commit.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public Task NotifyConfigUpdateFailureAsync(string errorMessage, string fileName, int? change, IUser? author, string? description, CancellationToken cancellationToken)
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

			return SendAsync(
				_channels.ResolveCategory(DiscordChannelCategory.UpdateStreams),
				embed,
				Only(author),
				cancellationToken);
		}

		#endregion

		#region Farm operations

		/// <summary>
		/// Reports something the device service wants a person to know.
		/// </summary>
		/// <remarks>
		/// **A departure from the Slack sink, deliberately.** Slack sends this as a direct message and sends nothing
		/// at all when it cannot identify the user, which in practice means every one of these is a private reminder
		/// about a device checkout. Discord cannot DM anyone until the Phase 3 user map exists, so this goes to the
		/// device channel with the person named - the same interim the job members take. Phase 3 turns it back into a
		/// DM, at which point the channel post becomes the fallback for an unmapped user rather than the norm.
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

			return SendAsync(_channels.ResolveCategory(DiscordChannelCategory.Device), embed, Only(user), cancellationToken);
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
				if (!UserId.TryParse(userId, out UserId parsed))
				{
					continue;
				}

				try
				{
					if (await _users.GetUserAsync(parsed, cancellationToken) is IUser user)
					{
						users.Add(user);
					}
				}
				catch (Exception ex)
				{
					_logger.LogDebug(ex, "Could not look up Horde user {UserId} while addressing a Discord notification.", userId);
				}
			}

			return users;
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
		public async Task SendAsync(IReadOnlyList<DiscordDestination> destinations, DiscordEmbedBuilder embed, IEnumerable<IUser>? forUsers, CancellationToken cancellationToken)
		{
			if (!_serverConfig.IsConfigured || destinations.Count == 0)
			{
				return;
			}

			// Named in plain text rather than mentioned. Until the Phase 3 user map exists there is no snowflake to
			// mention with, and a notification that arrives addressed to nobody beats one that does not arrive.
			IReadOnlyList<string> names = forUsers == null
				? Array.Empty<string>()
				: [.. forUsers.Select(x => x.Name).Where(x => !String.IsNullOrEmpty(x)).Distinct()];

			string? addressee = names.Count > 0 ? $"For {String.Join(", ", names.Select(Escape))}" : null;

			DiscordEmbed built = embed.Build();

			foreach (DiscordDestination destination in destinations)
			{
				DiscordMessageBuilder message = new DiscordMessageBuilder().AddEmbed(built);

				// A message in the catch-all says which Horde channel it was meant for. Without that the channel
				// fills up with notifications nobody can trace back to a missing mapping.
				string? note = destination.IsFallback && destination.SourceChannel != null
					? $"Unmapped Horde channel {Code(destination.SourceChannel)}"
					: null;

				string content = String.Join(" · ", new[] { addressee, note }.Where(x => x != null));

				if (content.Length > 0)
				{
					message.WithContent(content);
				}

				await _client.CreateMessageAsync(destination.ChannelId, message.Build(), cancellationToken);
			}
		}

		/// <summary>
		/// Posts an embed to a single destination, for the notifications that carry their own channel.
		/// </summary>
		/// <param name="destination">Where to post. Nothing is sent when this is null.</param>
		/// <param name="embed">What to post.</param>
		/// <param name="forUsers">Users the notification was aimed at, named in plain text.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		public Task SendToAsync(DiscordDestination? destination, DiscordEmbedBuilder embed, IEnumerable<IUser>? forUsers, CancellationToken cancellationToken)
			=> destination == null
				? Task.CompletedTask
				: SendAsync(new[] { destination }, embed, forUsers, cancellationToken);

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
