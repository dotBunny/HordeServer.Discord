// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using EpicGames.Horde.Agents;
using EpicGames.Horde.Commits;
using EpicGames.Horde.Jobs;
using EpicGames.Horde.Issues;
using EpicGames.Horde.Logs;
using HordeServer.Agents;
using HordeServer.Configuration;
using HordeServer.Devices;
using HordeServer.Discord.Client;
using HordeServer.Discord.Notifications;
using HordeServer.Issues;
using HordeServer.Logs;
using HordeServer.Notifications;
using HordeServer.Users;
using HordeTestDoubles;

namespace DiscordSmoke
{
	/// <summary>
	/// One of everything the plugin can post, driven through the real processor.
	/// </summary>
	/// <remarks>
	/// The point is to look at the result. Nothing here asserts - it builds notifications shaped like the ones Horde
	/// raises, hands them to <see cref="DiscordNotificationProcessor"/>, and lets the messages land in a channel
	/// where a person can judge whether they read well. The data is deliberately awkward in places: names with
	/// markdown characters in them, a compile error long enough to truncate, more failing steps than fit.
	/// </remarks>
	static class Scenarios
	{
		public static IReadOnlyList<Scenario> All(SmokeSettings settings) =>
		[
			new Scenario("job", "A job completed, to a channel", (processor, ct)
				=> processor.NotifyJobCompleteAsync(FailedJob(), LabelOutcome.Failure, ct)),

			new Scenario("step", "A step failed, with log events", (processor, ct)
				=> processor.NotifyJobStepCompleteAsync(FailedJob(), FailedStep(), new FakeNode("Compile Win64"), Events(), [Recipient()], ct)),

			new Scenario("step-timeout", "A step timed out, which reaches the channel with no subscribers", (processor, ct)
				=> processor.NotifyJobStepCompleteAsync(FailedJob(), TimedOutStep(), new FakeNode("Cook Content"), [], null, ct)),

			new Scenario("step-aborted", "A step was aborted", (processor, ct)
				=> processor.NotifyJobStepAbortedAsync(FailedJob(), AbortedStep(), new FakeNode("Package Build"), [], [Recipient()], ct)),

			new Scenario("label", "A label completed with failures", (processor, ct)
				=> processor.NotifyLabelCompleteAsync(FailedJob(), new FakeLabel(), LabelOutcome.Failure, FailedSteps(settings), Recipient(), ct)),

			new Scenario("scheduled", "Jobs waiting on a pool", (processor, ct)
				=> processor.NotifyJobScheduledAsync(Scheduled(), ct)),

			new Scenario("config-failure", "Configuration failed to load", (processor, ct)
				=> processor.NotifyConfigUpdateAsync(new ConfigUpdateInfo([], [], new InvalidOperationException(LongError)), ct)),

			new Scenario("config-recovered", "...and then loaded again", (processor, ct)
				=> processor.NotifyConfigUpdateAsync(new ConfigUpdateInfo(["Read 47 files from //depot/horde/..."], [], null), ct)),

			new Scenario("stream-config", "A stream's configuration failed, blaming a commit", (processor, ct)
				=> processor.NotifyConfigUpdateFailureAsync(
					"Unknown property 'notifcationChannel' on TemplateRefConfig",
					"//depot/streams/dethol_main.stream.json",
					1234567,
					Recipient(),
					"Add nightly cook template\n\n#rb none",
					ct)),

			new Scenario("agents", "Agents stuck conforming and upgrading", (processor, ct)
				=> processor.SendAgentReportAsync(AgentReport(), ct)),

			new Scenario("conflicts", "Session conflicts", (processor, ct)
				=> processor.SendSessionConflictReportAsync(
					[(new AgentId("build-07"), 412), (new AgentId("build-11"), 39)],
					ct)),

			new Scenario("devices", "Device pool health and device problems", (processor, ct)
				=> processor.SendDeviceIssueReportAsync(DeviceReport(settings), ct)),

			new Scenario("device-checkout", "A device checkout reminder, which should arrive as a DM", (processor, ct)
				=> processor.NotifyDeviceServiceAsync(
					$"Device PS5 / kit-04 checkout will expire in 24 hours. Please visit {settings.DashboardUrl}devices to check in and back out if needed.",
					null, null, null, null, null, null, Recipient(), ct)),

			new Scenario("test-health", "A test's health degraded", (processor, ct)
				=> processor.NotifyTestHealthReportAsync(
					new FakeTestHealthReport { State = "Failing", PreviousState = "Unreliable", CatastrophicFailureRate = 12 },
					SmokeChannelId,
					null,
					ct)),

			new Scenario("test-health-recovered", "...and then recovered", (processor, ct)
				=> processor.NotifyTestHealthReportAsync(
					new FakeTestHealthReport { IsHealthy = true, State = "Reliable", PreviousState = "Failing" },
					SmokeChannelId,
					null,
					ct)),

			new Scenario("issue", "An open issue, with live triage buttons", (processor, ct)
				=> processor.NotifyIssueUpdatedAsync(OpenIssue(), ct)),

			new Scenario("issue-resolved", "...and once resolved, offering nothing but the link", (processor, ct)
				=> processor.NotifyIssueUpdatedAsync(ResolvedIssue(), ct)),

			new Scenario("issue-report", "The periodic triage digest for a workflow", (processor, ct)
				=> processor.SendIssueReportAsync(IssueReport(), ct)),

			new Scenario("triage-ping", "An unassigned issue pinging its triage role", (processor, ct)
				=> TriagePingAsync(processor, settings, ct)),
		];

		/// <summary>
		/// Posts a message mentioning the configured role, the way an unassigned issue would.
		/// </summary>
		/// <remarks>
		/// Goes through <c>SendAsync</c> with an alias rather than through <c>NotifyIssueUpdatedAsync</c>, because
		/// which alias applies to an issue comes from a workflow in Horde's <c>BuildConfig</c> and there is no Horde
		/// behind this tool. What it does check is the half that only Discord can answer: whether
		/// <c>&lt;@&amp;id&gt;</c> renders as a role chip rather than raw text, and whether the
		/// <c>allowed_mentions</c> shape is accepted.
		/// </remarks>
		static Task TriagePingAsync(DiscordNotificationProcessor processor, SmokeSettings settings, CancellationToken cancellationToken)
		{
			if (settings.RoleId == null)
			{
				Console.Write("(no DiscordTestRoleId) ");
				return Task.CompletedTask;
			}

			return processor.SendAsync(
				[new DiscordDestination(settings.ChannelId, settings.GuildId, "smoke-test")],
				new DiscordEmbedBuilder()
					.WithTitle("🔴 Issue 4823: Nobody has picked this up")
					.WithDescription("An unassigned issue pings the workflow's triage alias. Once somebody takes it, "
						+ "the pings stop - that is what keeps a triage channel from being muted.")
					.WithColor(0xED4245)
					.AddField("Status", "Unassigned", true),
				null,
				new DiscordComponentBuilder().AddButton(
					new DiscordCustomId(DiscordCustomId.IssueScope, "4823", "ack").ToString(),
					"Acknowledge",
					DiscordButtonStyle.Success),
				[TriageAlias],
				cancellationToken);
		}

		/// <summary>
		/// The Horde-side alias the smoke role map is keyed on. Slack-shaped, like the channel id.
		/// </summary>
		public const string TriageAlias = "S0SMOKETRIAGE";

		/// <summary>
		/// An unassigned issue, so the buttons are all present and the message lands in a channel.
		/// </summary>
		/// <remarks>
		/// The buttons on this one are real: pressing them produces an interaction that nothing is listening for
		/// unless the gateway is also running, in which case the log says so. <c>--modal</c> is the mode that
		/// actually handles them.
		/// </remarks>
		static FakeIssue OpenIssue()
		{
			FakeIssue issue = IssueFakes.Issue(4821, "Compile error in Runtime/Core/Private/Misc/App.cpp", "dethol-main", "dethol-release");
			issue.Description = "Three steps across two streams are failing with the same error.";

			return issue;
		}

		static FakeIssue ResolvedIssue()
		{
			FakeIssue issue = IssueFakes.Issue(4822, "Cook failure on *PS5* [nightly]", "dethol-main");

			issue.ResolvedAt = DateTime.UtcNow.AddMinutes(-5.0);
			issue.FixCommitId = CommitId.FromPerforceChange(1234567);
			issue.RootCauseCategory = "Content";

			return issue;
		}

		/// <summary>
		/// A digest with more issues than fit, to see the overflow line in place.
		/// </summary>
		static IssueReportGroup IssueReport()
		{
			IssueReportGroup group = new IssueReportGroup(SmokeChannelId, DateTime.UtcNow);

			IssueReport main = IssueFakes.Report("dethol-main", "incremental", SmokeChannelId, 428, 391);

			for (int index = 0; index < 12; index++)
			{
				FakeIssue issue = IssueFakes.Issue(4800 + index, $"Failing step {index} in *Compile Win64* [shard {index}]", "dethol-main");
				issue.Severity = index % 3 == 0 ? IssueSeverity.Warning : IssueSeverity.Error;

				main.Issues.Add(issue);
			}

			group.Reports.Add(main);
			group.Reports.Add(IssueFakes.Report("dethol-release", "incremental", SmokeChannelId, 96, 96));

			return group;
		}

		/// <summary>
		/// The Horde-side channel id every scenario routes through, mapped to the real test channel.
		/// </summary>
		/// <remarks>
		/// A Slack-shaped id on purpose. Routing a notification the way Horde actually routes it - through the
		/// translation map rather than around it - is part of what this tool is checking.
		/// </remarks>
		public const string SmokeChannelId = "C0SMOKE0001";

		/// <summary>
		/// The email the smoke user is filed under, matching the map the tool builds.
		/// </summary>
		public const string RecipientEmail = "smoke@example.com";

		public static IUser Recipient() => HordeFakes.User("Ada *Lovelace*", RecipientEmail);

		static FakeJob FailedJob() => new FakeJob
		{
			Name = "Incremental Build [Win64]",
			PreflightCommitId = new CommitId("87654"),
			NotificationChannel = SmokeChannelId,
		};

		static FakeJobStep FailedStep() => new FakeJobStep { Outcome = JobStepOutcome.Failure };

		static FakeJobStep TimedOutStep() => new FakeJobStep { Outcome = JobStepOutcome.Failure, Error = JobStepError.TimedOut };

		static FakeJobStep AbortedStep() => new FakeJobStep
		{
			Outcome = JobStepOutcome.Unspecified,
			CancellationReason = "Superseded by CL 12346",
		};

		static IReadOnlyList<ILogEventData> Events() =>
		[
			new FakeLogEventData(LogEventSeverity.Error, "D:\\Build\\Engine\\Source\\Runtime\\Core\\Private\\Misc\\App.cpp(214): error C2065: 'FEngineLoop_Unused': undeclared identifier"),
			new FakeLogEventData(LogEventSeverity.Error, "D:\\Build\\Engine\\Source\\Runtime\\Core\\Private\\Misc\\App.cpp(219): error C2440: cannot convert from 'FString' to 'int32'"),
			new FakeLogEventData(LogEventSeverity.Warning, "UnrealBuildTool: warning: Deprecated module 'OnlineSubsystemNull' referenced by DetholGame"),
			new FakeLogEventData(LogEventSeverity.Information, "Building 4212 actions with 32 processes..."),
			new FakeLogEventData(LogEventSeverity.Error, "LogInit: error: Failed to load module 'DetholEditor'"),
			new FakeLogEventData(LogEventSeverity.Error, "ERROR: Took 412.2s to run dotnet, ExitCode=6"),
			new FakeLogEventData(LogEventSeverity.Warning, "Second warning, so the overflow count has something to report"),
		];

		static IReadOnlyList<(string, JobStepOutcome, Uri)> FailedSteps(SmokeSettings settings) =>
		[
			.. Enumerable.Range(1, 14).Select(x => (
				$"Compile Win64 Shard {x}",
				x % 3 == 0 ? JobStepOutcome.Warnings : JobStepOutcome.Failure,
				new Uri(settings.DashboardUrl, $"job/65f0000000000000000000a1?step=a1b{x}"))),
		];

		static IReadOnlyList<JobScheduledNotification> Scheduled() =>
		[
			.. Enumerable.Range(1, 12).Select(x => new JobScheduledNotification(
				"65f0000000000000000000a1",
				$"Nightly Cook Shard {x}",
				x % 2 == 0 ? "win-cook" : "linux-build")),
		];

		static AgentReport AgentReport()
		{
			AgentReport report = new AgentReport();

			foreach (int index in Enumerable.Range(1, 13))
			{
				report.ConformLoop.Add((new AgentId($"build-{index:00}"), 20 - index));
			}

			report.UpgradeLoop.Add((new AgentId("render-04"), 6));
			return report;
		}

		static DeviceIssueReport DeviceReport(SmokeSettings settings)
		{
			DeviceIssueReport report = new DeviceIssueReport(SmokeChannelId);

			DevicePoolReport pool = new DevicePoolReport("uk-farm", "UK Farm")
			{
				PoolURL = new Uri(settings.DashboardUrl, "devices").ToString(),
			};

			pool.Metrics.Add(new DevicePoolMetrics("ps5", "PlayStation 5")
			{
				Total = 24,
				Disabled = 2,
				AverageLoadPercentage = 61,
				Problems = 9,
				MaxConcurrentProblems = 5,
				MaxConcurrentProblemsPercentage = 21,
				SaturationSpikes = 4,
				SpikeDurationAverage = TimeSpan.FromMinutes(18.0),
				SpikeDurationPercentage = 12,
			});

			pool.Metrics.Add(new DevicePoolMetrics("xsx", "Xbox Series X")
			{
				Total = 12,
				Maintenance = 1,
				AverageLoadPercentage = 24,
				Problems = 1,
			});

			// Quiet, so it should not appear at all.
			pool.Metrics.Add(new DevicePoolMetrics("switch", "Switch") { Total = 8, AverageLoadPercentage = 3 });

			report.PoolReports.Add(pool);

			DevicePlatformReport platform = new DevicePlatformReport("ps5", "PlayStation 5");

			platform.DeviceReports.Add(new DeviceReport("ps5", "PlayStation 5", "kit-04", "kit-04", "10.4.2.14", "uk-farm", "UK Farm", [])
			{
				ProblemDelta = 11,
				ProblemPercent = 42,
				LastProblemDesc = "Reservation failed: device unreachable",
				LastProblemURL = new Uri(settings.DashboardUrl, "job/65f0000000000000000000a1").ToString(),
			});

			platform.DeviceReports.Add(new DeviceReport("ps5", "PlayStation 5", "kit_09", "kit_09", "10.4.2.19", "uk-farm", "UK Farm", [])
			{
				CleaningTime = TimeSpan.FromHours(7.0),
			});

			report.PlatformReports.Add(platform);
			return report;
		}

		const string LongError =
			"""
			Error parsing //depot/streams/dethol_main.stream.json:
			Unexpected character '}' at line 214, column 3. The property 'templates' was left open by the entry
			added in CL 1234567, which also removed the closing brace of the preceding object. Every stream that
			includes this file has failed to load, which means job scheduling for the whole branch has stopped
			and will stay stopped until the file parses. This message is long on purpose - it is here to check
			that the code block is cut off cleanly, that the fence still closes, and that the reader can tell
			something was removed rather than being left to wonder whether the error really ended mid-sentence
			like th
			""";
	}

	/// <summary>
	/// One thing the tool can send.
	/// </summary>
	/// <param name="Name">What to type to run just this one.</param>
	/// <param name="Description">What it posts, shown before it is sent.</param>
	/// <param name="RunAsync">Drives the processor.</param>
	sealed record Scenario(string Name, string Description, Func<DiscordNotificationProcessor, CancellationToken, Task> RunAsync);
}
