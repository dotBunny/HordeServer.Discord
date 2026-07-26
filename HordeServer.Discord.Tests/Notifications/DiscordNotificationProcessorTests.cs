// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json;
using EpicGames.Horde.Agents;
using EpicGames.Horde.Jobs;
using EpicGames.Horde.Logs;
using EpicGames.Horde.Users;
using HordeServer.Agents;
using HordeServer.Configuration;
using HordeServer.Devices;
using HordeServer.Discord.Client;
using HordeServer.Discord.Notifications;
using HordeServer.Discord.Tests.Client;
using HordeServer.Notifications;
using HordeServer.Plugins;
using HordeServer.Users;
using HordeTestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HordeServer.Discord.Tests.Notifications
{
	/// <summary>
	/// Tests for the messages the plugin would actually post, asserted on the JSON that reaches the wire.
	/// </summary>
	/// <remarks>
	/// Deliberately end to end from the notification to the request body. The interesting failures in this code are
	/// things that survive an object-level assertion and still break in Discord - a code fence cut in half by the
	/// field limit, an embed that overflows the combined ceiling, a link built into a field name where Discord will
	/// not render it - and only the serialised payload shows those.
	///
	/// It proves what would be sent, not what Discord does with it. <c>tools/DiscordSmoke</c> is the other half, and
	/// it earns its keep: the first real run turned up emoji shortcodes that every assertion here was blind to,
	/// because these tests blank the prefixes to keep the expected payloads readable.
	/// </remarks>
	[TestClass]
	public sealed class DiscordNotificationProcessorTests
	{
		const string ConfigChannel = "100000000000000001";
		const string AgentChannel = "100000000000000002";
		const string DeviceChannel = "100000000000000003";
		const string UpdateStreamsChannel = "100000000000000004";
		const string WorkflowChannel = "100000000000000005";
		const string JobChannel = "100000000000000006";
		const string GuildId = "100000000000000009";

		const string WorkflowSlackId = "C0832ESJUR5";

		const string AdaEmail = "ada@example.com";
		const string AdaDiscordId = "200000000000000001";

		#region Configuration

		[TestMethod]
		public async Task ConfigFailureNamesTheErrorAndGoesToTheConfigChannel()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyConfigUpdateAsync(Failed(new InvalidOperationException("unexpected '}' in streams.json")), default);

			Assert.AreEqual(1, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Requests[0].Uri, $"channels/{ConfigChannel}/messages");
			StringAssert.Contains(harness.Handler.Embed(0).GetProperty("title").GetString(), "Configuration update failed");
			StringAssert.Contains(harness.Handler.Field(0, "Error"), "unexpected '}' in streams.json");
		}

		[TestMethod]
		public async Task TheSameConfigFailureIsOnlyAnnouncedOnce()
		{
			Harness harness = new Harness();
			ConfigUpdateInfo info = Failed(new InvalidOperationException("unexpected '}' in streams.json"));

			await harness.Processor.NotifyConfigUpdateAsync(info, default);
			await harness.Processor.NotifyConfigUpdateAsync(info, default);
			await harness.Processor.NotifyConfigUpdateAsync(info, default);

			Assert.AreEqual(1, harness.Handler.Requests.Count,
				"Horde re-reads its configuration on a ticker, so a file that stays broken reports on every pass.");
		}

		[TestMethod]
		public async Task ADifferentConfigFailureIsAnnouncedAgain()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyConfigUpdateAsync(Failed(new InvalidOperationException("first")), default);
			await harness.Processor.NotifyConfigUpdateAsync(Failed(new InvalidOperationException("second")), default);

			Assert.AreEqual(2, harness.Handler.Requests.Count);
		}

		[TestMethod]
		public async Task ConfigSuccessIsSilentWhenNothingWasBroken()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyConfigUpdateAsync(new ConfigUpdateInfo(["read 12 files"], [], null), default);

			Assert.AreEqual(0, harness.Handler.Requests.Count,
				"A configuration that loads is the normal state of the world, and this fires on every poll.");
		}

		[TestMethod]
		public async Task ConfigSuccessAfterAFailureAnnouncesTheRecovery()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyConfigUpdateAsync(Failed(new InvalidOperationException("broken")), default);
			await harness.Processor.NotifyConfigUpdateAsync(new ConfigUpdateInfo(["read 12 files"], [], null), default);

			Assert.AreEqual(2, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Embed(1).GetProperty("title").GetString(), "succeeded");
			StringAssert.Contains(harness.Handler.Field(1, "Status"), "read 12 files");
		}

		[TestMethod]
		public async Task RecoveryIsOnlyAnnouncedOnce()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyConfigUpdateAsync(Failed(new InvalidOperationException("broken")), default);
			await harness.Processor.NotifyConfigUpdateAsync(new ConfigUpdateInfo([], [], null), default);
			await harness.Processor.NotifyConfigUpdateAsync(new ConfigUpdateInfo([], [], null), default);

			Assert.AreEqual(2, harness.Handler.Requests.Count);
		}

		[TestMethod]
		public async Task AnEnormousErrorKeepsItsCodeFenceClosed()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyConfigUpdateAsync(Failed(new InvalidOperationException(new string('x', 8000))), default);

			string value = harness.Handler.Field(0, "Error")!;

			Assert.IsTrue(value.Length <= DiscordEmbedLimits.FieldValue, $"The field was {value.Length} characters.");
			Assert.IsTrue(value.StartsWith("```", StringComparison.Ordinal));
			Assert.IsTrue(value.EndsWith("```", StringComparison.Ordinal),
				"A fence cut off by the field limit leaves Discord rendering the rest of the message as code.");
		}

		[TestMethod]
		public async Task AFenceInsideTheErrorCannotEscapeTheBlock()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyConfigUpdateAsync(Failed(new InvalidOperationException("see ``` this")), default);

			string value = harness.Handler.Field(0, "Error")!;

			Assert.AreEqual(2, CountOccurrences(value, "```"), "Only the opening and closing fences should remain.");
		}

		[TestMethod]
		public async Task StreamConfigFailureCarriesTheFileAndTheBlame()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyConfigUpdateFailureAsync(
				"could not resolve template 'missing'",
				"//depot/streams/dethol.stream.json",
				12345,
				HordeFakes.User("Ada Lovelace"),
				"Retarget the build templates",
				default);

			Assert.AreEqual(1, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Requests[0].Uri, $"channels/{UpdateStreamsChannel}/messages");
			StringAssert.Contains(harness.Handler.Field(0, "File"), "dethol.stream.json");
			StringAssert.Contains(harness.Handler.Field(0, "Error"), "could not resolve template");
			StringAssert.Contains(harness.Handler.Field(0, "Possibly due to"), "CL 12345 by Ada Lovelace");
			StringAssert.Contains(harness.Handler.Field(0, "Description"), "Retarget the build templates");
			StringAssert.Contains(harness.Handler.Message(0).GetProperty("content").GetString(), "cc Ada Lovelace");
		}

		[TestMethod]
		public async Task AFilePathInACodeSpanIsNotBackslashEscaped()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyConfigUpdateFailureAsync("broken", "//depot/streams/dethol_main.stream.json", null, null, null, default);

			Assert.AreEqual("`//depot/streams/dethol_main.stream.json`", harness.Handler.Field(0, "File"),
				"Discord renders a code span literally, so escaping markdown inside one only puts the backslashes "
				+ "on screen - and every config path has underscores in it.");
		}

		[TestMethod]
		public async Task StreamConfigFailureWithoutACommitOmitsTheBlame()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyConfigUpdateFailureAsync("broken", "globals.json", null, null, null, default);

			CollectionAssert.DoesNotContain(harness.Handler.FieldNames(0).ToList(), "Possibly due to");
		}

		#endregion

		#region Agents

		[TestMethod]
		public async Task AQuietFarmProducesNoAgentReport()
		{
			Harness harness = new Harness();

			await harness.Processor.SendAgentReportAsync(new AgentReport(), default);

			Assert.AreEqual(0, harness.Handler.Requests.Count);
		}

		[TestMethod]
		public async Task AgentReportListsWorstFirstAndSaysWhenASectionIsClear()
		{
			Harness harness = new Harness();

			AgentReport report = new AgentReport();
			report.ConformLoop.Add((new AgentId("render-02"), 3));
			report.ConformLoop.Add((new AgentId("render-01"), 11));

			await harness.Processor.SendAgentReportAsync(report, default);

			Assert.AreEqual(1, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Requests[0].Uri, $"channels/{AgentChannel}/messages");

			string conform = harness.Handler.Field(0, "Conform issues (2)")!;

			// Upper case is not a typo: AgentId canonicalises its name, so that is the id both the label and the
			// dashboard link have to carry.
			Assert.IsTrue(conform.IndexOf("RENDER-01", StringComparison.Ordinal) < conform.IndexOf("RENDER-02", StringComparison.Ordinal),
				"The agent in the deepest loop is the one to look at first.");
			StringAssert.Contains(conform, "https://horde.example.com/agents?agentId=RENDER-01");
			Assert.AreEqual("None.", harness.Handler.Field(0, "Upgrade issues"),
				"Saying the upgrade side is clear is what makes the conform side actionable.");
		}

		[TestMethod]
		public async Task ALongAgentListSaysHowManyItLeftOut()
		{
			Harness harness = new Harness();

			AgentReport report = new AgentReport();

			for (int index = 0; index < 14; index++)
			{
				report.ConformLoop.Add((new AgentId($"render-{index:00}"), index));
			}

			await harness.Processor.SendAgentReportAsync(report, default);

			StringAssert.Contains(harness.Handler.Field(0, "Conform issues (14)"), "and 4 more");
		}

		[TestMethod]
		public async Task NoSessionConflictsProducesNoReport()
		{
			Harness harness = new Harness();

			await harness.Processor.SendSessionConflictReportAsync([], default);

			Assert.AreEqual(0, harness.Handler.Requests.Count);
		}

		[TestMethod]
		public async Task SessionConflictsAreReportedToTheAgentChannel()
		{
			Harness harness = new Harness();

			await harness.Processor.SendSessionConflictReportAsync([(new AgentId("build-07"), 42)], default);

			Assert.AreEqual(1, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Requests[0].Uri, $"channels/{AgentChannel}/messages");
			StringAssert.Contains(harness.Handler.Field(0, "Agents (1)"), "42 mismatch(es)");
		}

		#endregion

		#region Devices

		[TestMethod]
		public async Task DeviceReportGoesToTheChannelItCarries()
		{
			Harness harness = new Harness();

			DeviceIssueReport report = new DeviceIssueReport(WorkflowSlackId);
			report.PoolReports.Add(Pool("uk-farm", Metrics("PS5", load: 62)));

			await harness.Processor.SendDeviceIssueReportAsync(report, default);

			Assert.AreEqual(1, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Requests[0].Uri, $"channels/{WorkflowChannel}/messages",
				"Reports carry their own Slack channel id, which the map translates - the device category is only the "
				+ "fallback for notifications that carry nothing.");
		}

		[TestMethod]
		public async Task ASaturatedPlatformIsCalledOutInRed()
		{
			Harness harness = new Harness();

			DeviceIssueReport report = new DeviceIssueReport(WorkflowSlackId);
			report.PoolReports.Add(Pool("uk-farm", Metrics("PS5", load: 62, problems: 4)));

			await harness.Processor.SendDeviceIssueReportAsync(report, default);

			Assert.AreEqual(DiscordNotificationProcessor.FailureColor, harness.Handler.Embed(0).GetProperty("color").GetInt32());
			CollectionAssert.Contains(harness.Handler.FieldNames(0).ToList(), "🔴 PS5");
			StringAssert.Contains(harness.Handler.Field(0, "🔴 PS5"), "Average load 62%");
		}

		[TestMethod]
		public async Task AQuietPlatformIsLeftOutEntirely()
		{
			Harness harness = new Harness();

			DeviceIssueReport report = new DeviceIssueReport(WorkflowSlackId);
			report.PoolReports.Add(Pool("uk-farm", Metrics("PS5", load: 4), Metrics("XSX", load: 55)));

			await harness.Processor.SendDeviceIssueReportAsync(report, default);

			CollectionAssert.DoesNotContain(harness.Handler.FieldNames(0).ToList(), "🟡 PS5");
			CollectionAssert.Contains(harness.Handler.FieldNames(0).ToList(), "🔴 XSX");
		}

		[TestMethod]
		public async Task AnEntirelyQuietReportStillSaysSo()
		{
			Harness harness = new Harness();

			DeviceIssueReport report = new DeviceIssueReport(WorkflowSlackId);
			report.PoolReports.Add(Pool("uk-farm", Metrics("PS5", load: 4)));

			await harness.Processor.SendDeviceIssueReportAsync(report, default);

			Assert.AreEqual(1, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Embed(0).GetProperty("description").GetString(), "No outstanding usage",
				"A report that goes quiet is otherwise indistinguishable from a sink that has stopped working.");
		}

		[TestMethod]
		public async Task DeviceProblemsAreListedWorstFirstWithTheirLinks()
		{
			Harness harness = new Harness();

			DevicePlatformReport platform = new DevicePlatformReport("ps5", "PS5");
			platform.DeviceReports.Add(Device("kit-quiet", problems: 1));
			platform.DeviceReports.Add(Device("kit-loud", problems: 9, lastProblemUrl: "https://horde.example.com/job/1"));

			DeviceIssueReport report = new DeviceIssueReport(WorkflowSlackId);
			report.PlatformReports.Add(platform);

			await harness.Processor.SendDeviceIssueReportAsync(report, default);

			Assert.AreEqual(1, harness.Handler.Requests.Count);
			CollectionAssert.AreEqual(new[] { "🔴 kit-loud", "🔴 kit-quiet" }, harness.Handler.FieldNames(0).ToList());
			StringAssert.Contains(harness.Handler.Field(0, "🔴 kit-loud"), "(https://horde.example.com/job/1)");
		}

		[TestMethod]
		public async Task ADeviceBeingCleanedIsNotReportedAsFailing()
		{
			Harness harness = new Harness();

			DevicePlatformReport platform = new DevicePlatformReport("ps5", "PS5");
			platform.DeviceReports.Add(Device("kit-cleaning", problems: 0, cleaningFor: TimeSpan.FromHours(3.0)));

			DeviceIssueReport report = new DeviceIssueReport(WorkflowSlackId);
			report.PlatformReports.Add(platform);

			await harness.Processor.SendDeviceIssueReportAsync(report, default);

			CollectionAssert.Contains(harness.Handler.FieldNames(0).ToList(), "🔵 kit-cleaning");
			StringAssert.Contains(harness.Handler.Field(0, "🔵 kit-cleaning"), "Cleaning for 3 hour(s)");
		}

		[TestMethod]
		public async Task DeviceServiceMessagesReachTheDeviceChannelNamingTheUser()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyDeviceServiceAsync(
				"Device PS5 / kit-04 checkout will expire in 24 hours.",
				null,
				null,
				null,
				null,
				null,
				null,
				HordeFakes.User("Ada Lovelace"),
				default);

			Assert.AreEqual(1, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Requests[0].Uri, $"channels/{DeviceChannel}/messages");
			StringAssert.Contains(harness.Handler.Embed(0).GetProperty("description").GetString(), "checkout will expire");
			StringAssert.Contains(harness.Handler.Message(0).GetProperty("content").GetString(), "cc Ada Lovelace",
				"Slack sends this as a DM. Until the Phase 3 user map exists, naming them in the channel is how the "
				+ "message still reaches the person it is about.");
		}

		#endregion

		#region Test health

		[TestMethod]
		public async Task ADegradedTestIsReportedToTheWorkflowChannel()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyTestHealthReportAsync(
				new FakeTestHealthReport { State = "Unreliable" },
				WorkflowSlackId,
				null,
				default);

			Assert.AreEqual(1, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Requests[0].Uri, $"channels/{WorkflowChannel}/messages");
			Assert.AreEqual(DiscordNotificationProcessor.FailureColor, harness.Handler.Embed(0).GetProperty("color").GetInt32());
			StringAssert.Contains(harness.Handler.Embed(0).GetProperty("url").GetString(), "test-automation?stream=dethol-main");
			Assert.AreEqual("40%", harness.Handler.Field(0, "Success rate"));
		}

		[TestMethod]
		public async Task AStateChangeSaysWhatItChangedFrom()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyTestHealthReportAsync(
				new FakeTestHealthReport { State = "Failing", PreviousState = "Unreliable" },
				WorkflowSlackId,
				null,
				default);

			string description = harness.Handler.Embed(0).GetProperty("description").GetString()!;

			StringAssert.Contains(description, "Unreliable");
			StringAssert.Contains(description, "Failing");
		}

		[TestMethod]
		public async Task ARecoveryIsSilentIfTheDegradationWasNeverAnnounced()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyTestHealthReportAsync(
				new FakeTestHealthReport { IsHealthy = true, State = "Reliable" },
				WorkflowSlackId,
				null,
				default);

			Assert.AreEqual(0, harness.Handler.Requests.Count,
				"'It is fixed' means nothing to a channel that was never told it was broken.");
		}

		[TestMethod]
		public async Task ARecoveryPairsUpWithTheDegradationThatPrecededIt()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyTestHealthReportAsync(new FakeTestHealthReport { State = "Failing" }, WorkflowSlackId, null, default);
			await harness.Processor.NotifyTestHealthReportAsync(
				new FakeTestHealthReport { IsHealthy = true, State = "Reliable" },
				WorkflowSlackId,
				null,
				default);

			Assert.AreEqual(2, harness.Handler.Requests.Count);
			Assert.AreEqual(DiscordNotificationProcessor.SuccessColor, harness.Handler.Embed(1).GetProperty("color").GetInt32());
			StringAssert.Contains(harness.Handler.Embed(1).GetProperty("description").GetString(), "recovered");
		}

		[TestMethod]
		public async Task CarbonCopiedOwnersAreNamed()
		{
			FakeUserCollection users = new FakeUserCollection();
			UserId owner = users.Add(HordeFakes.User("Ada Lovelace"));
			UserId unknown = UserId.Parse("0000000000000000000000ff");

			Harness harness = new Harness(users);

			await harness.Processor.NotifyTestHealthReportAsync(
				new FakeTestHealthReport { State = "Failing" },
				WorkflowSlackId,
				[owner.ToString(), unknown.ToString(), "not-a-user-id"],
				default);

			Assert.AreEqual("cc Ada Lovelace", harness.Handler.Message(0).GetProperty("content").GetString(),
				"An unknown id and an unparseable one both drop out quietly; the report is about the test.");
		}

		#endregion

		#region Gating

		[TestMethod]
		public async Task NothingIsSentWithoutABotToken()
		{
			Harness harness = new Harness(botToken: null);

			await harness.Processor.SendSessionConflictReportAsync([(new AgentId("build-07"), 42)], default);
			await harness.Processor.NotifyConfigUpdateAsync(Failed(new InvalidOperationException("broken")), default);

			Assert.AreEqual(0, harness.Handler.Requests.Count,
				"Running the plugin unconfigured is a supported way to verify it loads before any credentials exist.");
		}

		[TestMethod]
		public async Task AnUnroutableNotificationIsDroppedRatherThanThrown()
		{
			Harness harness = new Harness(config: Harness.PostLoad(new DiscordConfig()), agentChannel: null);

			await harness.Processor.SendSessionConflictReportAsync([(new AgentId("build-07"), 42)], default);

			Assert.AreEqual(0, harness.Handler.Requests.Count);
		}

		#endregion

		#region Jobs and steps

		[TestMethod]
		public async Task AJobCompletionIsAnnouncedToTheChannelTheJobNames()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyJobCompleteAsync(
				new FakeJob { NotificationChannel = WorkflowSlackId },
				LabelOutcome.Failure,
				default);

			Assert.AreEqual(1, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Requests[0].Uri, $"channels/{WorkflowChannel}/messages");
			Assert.AreEqual("Failed", harness.Handler.Field(0, "Outcome"));
			Assert.AreEqual("dethol-main", harness.Handler.Field(0, "Stream"));
		}

		[TestMethod]
		public async Task AnOutcomeFilterIsHonoured()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyJobCompleteAsync(
				new FakeJob { NotificationChannel = WorkflowSlackId, NotificationChannelFilter = "Failure" },
				LabelOutcome.Success,
				default);

			Assert.AreEqual(0, harness.Handler.Requests.Count,
				"An outcome filter that excluded this is a decision, not a gap - falling through to the Discord "
				+ "override would post the very outcomes somebody asked not to hear about.");
		}

		[TestMethod]
		public async Task AJobWithNoChannelAtAllUsesTheDiscordOverride()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyJobCompleteAsync(new FakeJob(), LabelOutcome.Failure, default);

			Assert.AreEqual(1, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Requests[0].Uri, $"channels/{JobChannel}/messages",
				"A fresh install with only the Discord channel filled in should not be silent, even though Horde "
				+ "itself would send nothing.");
		}

		[TestMethod]
		public async Task ThePersonWhoAbortedAJobIsNotToldItStopped()
		{
			Harness harness = new Harness();
			IUser ada = HordeFakes.User("Ada Lovelace", AdaEmail);

			await harness.Processor.NotifyJobCompleteToUserAsync(
				ada,
				new FakeJob { AbortedByUserId = ada.Id },
				LabelOutcome.Failure,
				default);

			Assert.AreEqual(0, harness.Handler.Requests.Count, "They pressed the button; they know.");
		}

		[TestMethod]
		public async Task ASubscribedJobCompletionIsADirectMessage()
		{
			Harness harness = Reachable();

			await harness.Processor.NotifyJobCompleteToUserAsync(
				HordeFakes.User("Ada Lovelace", AdaEmail),
				new FakeJob(),
				LabelOutcome.Failure,
				default);

			Assert.AreEqual(2, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Requests[1].Uri, $"channels/{DmChannel}/messages");
		}

		[TestMethod]
		public async Task AStepWithNobodySubscribedIsNotAnnounced()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyJobStepCompleteAsync(
				new FakeJob(),
				new FakeJobStep(),
				new FakeNode("Compile Win64"),
				[],
				null,
				default);

			Assert.AreEqual(0, harness.Handler.Requests.Count,
				"These are subscription notifications. Broadcasting one per subscriber to a shared channel is what "
				+ "makes a job channel unusable, and with no subscribers there is nobody it was for.");
		}

		[TestMethod]
		public async Task ATimedOutStepReachesTheChannelWithNobodySubscribed()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyJobStepCompleteAsync(
				new FakeJob(),
				new FakeJobStep { Error = JobStepError.TimedOut },
				new FakeNode("Cook Content"),
				[],
				null,
				default);

			Assert.AreEqual(1, harness.Handler.Requests.Count,
				"A step hitting its time limit is a farm problem rather than a subscriber's. Slack checks for "
				+ "subscribers first and so misses this, which reads as an ordering accident.");
			StringAssert.Contains(harness.Handler.Requests[0].Uri, $"channels/{JobChannel}/messages");
			Assert.AreEqual("Timed out", harness.Handler.Field(0, "Outcome"));
		}

		[TestMethod]
		public async Task AFailingStepQuotesItsWorstLogEvents()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyJobStepCompleteAsync(
				new FakeJob(),
				new FakeJobStep(),
				new FakeNode("Compile Win64"),
				[
					new FakeLogEventData(LogEventSeverity.Information, "Building 4212 actions"),
					new FakeLogEventData(LogEventSeverity.Error, "error C2065: undeclared identifier"),
					new FakeLogEventData(LogEventSeverity.Warning, "warning: deprecated module"),
				],
				[HordeFakes.User("Ada Lovelace", AdaEmail)],
				default);

			string events = harness.Handler.Field(0, "Events (2)")!;

			StringAssert.Contains(events, "error C2065");
			Assert.IsFalse(events.Contains("4212 actions", StringComparison.Ordinal),
				"Information-level events are noise; whoever is reading this wants the reason it went red.");
		}

		[TestMethod]
		public async Task AnAbortedStepSaysWhy()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyJobStepAbortedAsync(
				new FakeJob(),
				new FakeJobStep { CancellationReason = "Superseded by CL 12346" },
				new FakeNode("Package Build"),
				[],
				[HordeFakes.User("Ada Lovelace", AdaEmail)],
				default);

			Assert.AreEqual("Superseded by CL 12346", harness.Handler.Field(0, "Outcome"));
			Assert.AreEqual(DiscordNotificationProcessor.NeutralColor, harness.Handler.Embed(0).GetProperty("color").GetInt32(),
				"Somebody chose this, so it is not a failure.");
		}

		[TestMethod]
		public async Task ALabelListsOnlyTheStepsThatWentWrong()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyLabelCompleteAsync(
				new FakeJob(),
				new FakeLabel(),
				LabelOutcome.Failure,
				[
					("Compile Win64", JobStepOutcome.Failure, new Uri("https://horde.example.com/job/1?step=a")),
					("Compile Linux", JobStepOutcome.Success, new Uri("https://horde.example.com/job/1?step=b")),
				],
				HordeFakes.User("Ada Lovelace", AdaEmail),
				default);

			string steps = harness.Handler.Field(0, "Steps (1)")!;

			StringAssert.Contains(steps, "Compile Win64");
			Assert.IsFalse(steps.Contains("Compile Linux", StringComparison.Ordinal),
				"On a healthy label that is none of them, and the embed stays a one-liner.");
		}

		[TestMethod]
		public async Task WaitingJobsAreGroupedByThePoolTheyAreWaitingOn()
		{
			Harness harness = new Harness();

			await harness.Processor.NotifyJobScheduledAsync(
				[
					new JobScheduledNotification("65f0000000000000000000a1", "Nightly Cook", "win-cook"),
					new JobScheduledNotification("65f0000000000000000000a2", "Nightly Build", "win-cook"),
					new JobScheduledNotification("65f0000000000000000000a3", "Linux Build", "linux-build"),
				],
				default);

			CollectionAssert.AreEquivalent(new[] { "win-cook (2)", "linux-build (1)" }, harness.Handler.FieldNames(0).ToList(),
				"Jobs pile up when one pool has no agents, and a flat list of twenty job names buries which pool it is.");
		}

		#endregion

		#region Mentions

		[TestMethod]
		public async Task AMappedPersonIsMentionedRatherThanNamed()
		{
			Harness harness = new Harness(config: Harness.Mapped((AdaEmail, AdaDiscordId)));

			await harness.Processor.SendAsync(Channel(), Embed(), [HordeFakes.User("Ada Lovelace", AdaEmail)], default);

			Assert.AreEqual($"cc <@{AdaDiscordId}>", harness.Handler.Message(0).GetProperty("content").GetString());
		}

		[TestMethod]
		public async Task OnlyThePeopleTheNotificationIsAboutMayBePinged()
		{
			Harness harness = new Harness(config: Harness.Mapped((AdaEmail, AdaDiscordId)));

			await harness.Processor.SendAsync(Channel(), Embed(), [HordeFakes.User("Ada Lovelace", AdaEmail)], default);

			JsonElement allowed = harness.Handler.Message(0).GetProperty("allowed_mentions");

			Assert.AreEqual(0, allowed.GetProperty("parse").GetArrayLength(),
				"Nothing is parsed out of the content - a step name or an error line must never ping anyone.");
			Assert.AreEqual(AdaDiscordId, allowed.GetProperty("users")[0].GetString());
		}

		[TestMethod]
		public async Task AnUnmappedPersonIsStillNamed()
		{
			Harness harness = new Harness();

			await harness.Processor.SendAsync(Channel(), Embed(), [HordeFakes.User("Ada Lovelace", AdaEmail)], default);

			Assert.AreEqual("cc Ada Lovelace", harness.Handler.Message(0).GetProperty("content").GetString(),
				"A half-filled map costs a mention, never a notification.");
			Assert.IsFalse(harness.Handler.Message(0).GetProperty("allowed_mentions").TryGetProperty("users", out _));
		}

		[TestMethod]
		public async Task AMixedGroupIsPartlyMentionedAndPartlyNamed()
		{
			Harness harness = new Harness(config: Harness.Mapped((AdaEmail, AdaDiscordId)));

			await harness.Processor.SendAsync(
				Channel(),
				Embed(),
				[HordeFakes.User("Ada Lovelace", AdaEmail), HordeFakes.User("Grace Hopper", "grace@example.com")],
				default);

			Assert.AreEqual($"cc <@{AdaDiscordId}>, Grace Hopper", harness.Handler.Message(0).GetProperty("content").GetString());
		}

		[TestMethod]
		public async Task TheSamePersonTwiceIsAddressedOnce()
		{
			Harness harness = new Harness(config: Harness.Mapped((AdaEmail, AdaDiscordId)));
			IUser ada = HordeFakes.User("Ada Lovelace", AdaEmail);

			await harness.Processor.SendAsync(Channel(), Embed(), [ada, ada], default);

			Assert.AreEqual($"cc <@{AdaDiscordId}>", harness.Handler.Message(0).GetProperty("content").GetString());
		}

		#endregion

		#region Direct messages

		[TestMethod]
		public async Task AMappedPersonIsMessagedDirectly()
		{
			Harness harness = Reachable();

			await harness.Processor.SendToUsersAsync([HordeFakes.User("Ada Lovelace", AdaEmail)], Channel(), Embed(), default);

			Assert.AreEqual(2, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Requests[0].Uri, "users/@me/channels",
				"A Discord DM is an ordinary channel that has to be opened before it can be posted to.");
			StringAssert.Contains(harness.Handler.Requests[1].Uri, $"channels/{DmChannel}/messages");
		}

		[TestMethod]
		public async Task ADirectMessageDoesNotAlsoGoToTheChannel()
		{
			Harness harness = Reachable();

			await harness.Processor.SendToUsersAsync([HordeFakes.User("Ada Lovelace", AdaEmail)], Channel(), Embed(), default);

			Assert.IsFalse(harness.Handler.Requests.Any(x => x.Uri.Contains(AgentChannel, StringComparison.Ordinal)),
				"Broadcasting a subscription notification as well as sending it is what makes a job channel unusable.");
		}

		[TestMethod]
		public async Task AnUnmappedPersonFallsBackToTheChannel()
		{
			Harness harness = new Harness();

			await harness.Processor.SendToUsersAsync([HordeFakes.User("Ada Lovelace", AdaEmail)], Channel(), Embed(), default);

			Assert.AreEqual(1, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Requests[0].Uri, $"channels/{AgentChannel}/messages");
			Assert.AreEqual("cc Ada Lovelace", harness.Handler.Message(0).GetProperty("content").GetString());
		}

		[TestMethod]
		public async Task ARefusedDirectMessageChannelFallsBackToTheChannel()
		{
			// 403 is what Discord returns when the bot shares no guild with someone, or they have direct messages
			// from server members turned off. Both are ordinary states, not errors.
			Harness harness = new Harness(
				config: Harness.Mapped((AdaEmail, AdaDiscordId)),
				responses: RecordingHttpHandler.Json(HttpStatusCode.Forbidden, """{"message":"Cannot send messages to this user","code":50007}"""));

			await harness.Processor.SendToUsersAsync([HordeFakes.User("Ada Lovelace", AdaEmail)], Channel(), Embed(), default);

			Assert.AreEqual(2, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Requests[1].Uri, $"channels/{AgentChannel}/messages");
			Assert.AreEqual($"cc <@{AdaDiscordId}>", harness.Handler.Message(1).GetProperty("content").GetString(),
				"They are known, just not reachable directly - so the channel post can still ping them.");
		}

		[TestMethod]
		public async Task ARejectedDirectMessageAlsoFallsBack()
		{
			Harness harness = new Harness(
				config: Harness.Mapped((AdaEmail, AdaDiscordId)),
				responses:
				[
					RecordingHttpHandler.Json(HttpStatusCode.OK, $$"""{"id":"{{DmChannel}}"}"""),
					RecordingHttpHandler.Json(HttpStatusCode.Forbidden, """{"message":"Cannot send messages to this user","code":50007}"""),
				]);

			await harness.Processor.SendToUsersAsync([HordeFakes.User("Ada Lovelace", AdaEmail)], Channel(), Embed(), default);

			Assert.AreEqual(3, harness.Handler.Requests.Count,
				"Opening the channel can succeed and the send still be refused, so the fallback cannot hang off the "
				+ "channel lookup alone.");
			StringAssert.Contains(harness.Handler.Requests[2].Uri, $"channels/{AgentChannel}/messages");
		}

		[TestMethod]
		public async Task TheDirectMessageChannelIsLookedUpOnce()
		{
			Harness harness = Reachable();
			IUser ada = HordeFakes.User("Ada Lovelace", AdaEmail);

			await harness.Processor.SendToUsersAsync([ada], Channel(), Embed(), default);
			await harness.Processor.SendToUsersAsync([ada], Channel(), Embed(), default);

			Assert.AreEqual(1, harness.Handler.Requests.Count(x => x.Uri.Contains("users/@me/channels", StringComparison.Ordinal)),
				"Opening a DM is idempotent and returns the same channel, so re-asking on every notification is pure waste.");
		}

		[TestMethod]
		public async Task NobodyToAddressMeansTheChannel()
		{
			Harness harness = new Harness();

			await harness.Processor.SendToUsersAsync(null, Channel(), Embed(), default);

			Assert.AreEqual(1, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Requests[0].Uri, $"channels/{AgentChannel}/messages");
		}

		[TestMethod]
		public async Task TheConfigFailureAuthorGetsBothTheChannelPostAndAMessage()
		{
			Harness harness = new Harness(
				config: Harness.Mapped((AdaEmail, AdaDiscordId)),
				responses:
				[
					RecordingHttpHandler.Json(HttpStatusCode.OK, """{"id":"9"}"""),
					RecordingHttpHandler.Json(HttpStatusCode.OK, $$"""{"id":"{{DmChannel}}"}"""),
				]);

			await harness.Processor.NotifyConfigUpdateFailureAsync("broken", "globals.json", 42, HordeFakes.User("Ada Lovelace", AdaEmail), null, default);

			Assert.AreEqual(3, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Requests[0].Uri, $"channels/{UpdateStreamsChannel}/messages",
				"The team needs to know the configuration is stale.");
			StringAssert.Contains(harness.Handler.Requests[2].Uri, $"channels/{DmChannel}/messages",
				"And the person who broke it should not have to be watching a channel to find out.");
		}

		[TestMethod]
		public async Task ADeviceReminderIsPrivateWhenItCanBe()
		{
			Harness harness = Reachable();

			await harness.Processor.NotifyDeviceServiceAsync("Your checkout expires in 24 hours.", null, null, null, null, null, null, HordeFakes.User("Ada Lovelace", AdaEmail), default);

			Assert.AreEqual(2, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Requests[1].Uri, $"channels/{DmChannel}/messages");
		}

		#endregion

		#region Deep links

		[TestMethod]
		public async Task DeepLinksStayOutOfTheWayWhenSlackIsRunning()
		{
			Harness harness = new Harness(config: Harness.Mapped((AdaEmail, AdaDiscordId)), slackToken: "xoxb-something");

			Assert.IsNull(await harness.Processor.GetChannelLinkAsync(WorkflowSlackId, default),
				"Horde takes the first non-null answer from any sink, so answering here would decide by luck of "
				+ "registration order whether the dashboard's buttons still opened Slack.");
		}

		[TestMethod]
		public async Task DeepLinksAreProvidedWhenNothingElseWould()
		{
			Harness harness = new Harness(config: Harness.Mapped());

			Assert.AreEqual($"https://discord.com/channels/{GuildId}/{WorkflowChannel}",
				await harness.Processor.GetChannelLinkAsync(WorkflowSlackId, default));
		}

		[TestMethod]
		public async Task DeepLinksCanBeTurnedOnAlongsideSlack()
		{
			Harness harness = new Harness(config: Harness.Mapped(), slackToken: "xoxb-something", enableDeepLinks: true);

			Assert.IsNotNull(await harness.Processor.GetChannelLinkAsync(WorkflowSlackId, default));
		}

		[TestMethod]
		public async Task AnUnmappedChannelHasNoLink()
		{
			Harness harness = new Harness(config: Harness.Mapped());

			Assert.IsNull(await harness.Processor.GetChannelLinkAsync("C0ZZZZZZZZZ", default),
				"Linking to the catch-all would be a link that works and is wrong.");
		}

		[TestMethod]
		public async Task ADirectMessageLinkOpensTheConversation()
		{
			FakeUserCollection users = new FakeUserCollection();
			UserId ada = users.Add(HordeFakes.User("Ada Lovelace", AdaEmail));

			Harness harness = new Harness(
				users,
				Harness.Mapped((AdaEmail, AdaDiscordId)),
				responses: RecordingHttpHandler.Json(HttpStatusCode.OK, $$"""{"id":"{{DmChannel}}"}"""));

			Assert.AreEqual($"https://discord.com/channels/@me/{DmChannel}",
				await harness.Processor.GetDirectMessageLinkAsync([ada], default));
		}

		[TestMethod]
		public async Task ThereIsNoLinkForAGroupConversation()
		{
			FakeUserCollection users = new FakeUserCollection();
			UserId ada = users.Add(HordeFakes.User("Ada Lovelace", AdaEmail));
			UserId grace = users.Add(HordeFakes.User("Grace Hopper", "grace@example.com"));

			Harness harness = new Harness(users, Harness.Mapped((AdaEmail, AdaDiscordId)));

			Assert.IsNull(await harness.Processor.GetDirectMessageLinkAsync([ada, grace], default),
				"Slack supports up to eight people in a DM. Discord's group DMs need OAuth scopes a bot cannot have, "
				+ "so there is no honest answer for more than one.");
		}

		[TestMethod]
		public async Task ThereIsNoLinkForSomebodyUnmapped()
		{
			FakeUserCollection users = new FakeUserCollection();
			UserId ada = users.Add(HordeFakes.User("Ada Lovelace", AdaEmail));

			Harness harness = new Harness(users, Harness.Mapped());

			Assert.IsNull(await harness.Processor.GetDirectMessageLinkAsync([ada], default));
		}

		#endregion

		const string DmChannel = "300000000000000001";

		static Harness Reachable() => new Harness(
			config: Harness.Mapped((AdaEmail, AdaDiscordId)),
			responses: RecordingHttpHandler.Json(HttpStatusCode.OK, $$"""{"id":"{{DmChannel}}"}"""));

		static IReadOnlyList<DiscordDestination> Channel() => [new DiscordDestination(AgentChannel)];

		static DiscordEmbedBuilder Embed() => new DiscordEmbedBuilder().WithTitle("Something happened");

		static ConfigUpdateInfo Failed(Exception exception) => new ConfigUpdateInfo([], [], exception);

		static int CountOccurrences(string text, string value)
		{
			int count = 0;

			for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
			{
				count++;
			}

			return count;
		}

		static DevicePoolReport Pool(string name, params DevicePoolMetrics[] metrics)
		{
			DevicePoolReport pool = new DevicePoolReport(name, name);
			pool.Metrics.AddRange(metrics);
			return pool;
		}

		static DevicePoolMetrics Metrics(string platform, int load = 0, int problems = 0, int total = 10)
			=> new DevicePoolMetrics(platform.ToLowerInvariant(), platform)
			{
				AverageLoadPercentage = load,
				Problems = problems,
				Total = total,
			};

		static DeviceReport Device(string name, int problems, TimeSpan? cleaningFor = null, string? lastProblemUrl = null)
			=> new DeviceReport("ps5", "PS5", name, name, "10.0.0.1", "uk-farm", "uk-farm", [])
			{
				ProblemDelta = problems,
				ProblemPercent = problems * 10,
				CleaningTime = cleaningFor,
				LastProblemURL = lastProblemUrl,
				LastProblemDesc = lastProblemUrl == null ? null : "Reservation failed",
			};

		/// <summary>
		/// A processor wired to a recording transport, with every category channel configured.
		/// </summary>
		sealed class Harness
		{
			public Harness(FakeUserCollection? users = null, DiscordConfig? config = null, string? botToken = "token", string? agentChannel = AgentChannel, string? slackToken = null, bool? enableDeepLinks = null, params HttpResponseMessage[] responses)
			{
				DiscordServerConfig serverConfig = new DiscordServerConfig
				{
					BotToken = botToken,
					ConfigNotificationChannel = ConfigChannel,
					JobNotificationChannel = JobChannel,
					AgentNotificationChannel = agentChannel,
					DeviceNotificationChannel = DeviceChannel,
					UpdateStreamsNotificationChannel = UpdateStreamsChannel,
					EnableDeepLinks = enableDeepLinks,

					// Plain text, so an assertion on a message reads as the message rather than as an emoji shortcode.
					ErrorPrefix = String.Empty,
					WarningPrefix = String.Empty,
				};

				IOptions<DiscordServerConfig> options = Options.Create(serverConfig);
				IOptions<BuildServerConfig> buildServerConfig = Options.Create(new BuildServerConfig { SlackToken = slackToken });
				StaticOptionsMonitor<DiscordConfig> pluginConfig = new StaticOptionsMonitor<DiscordConfig>(config ?? Mapped());

				DiscordChannelResolver channels = new DiscordChannelResolver(
					pluginConfig,
					options,
					buildServerConfig,
					NullLogger<DiscordChannelResolver>.Instance);

				Handler = new RecordingHttpHandler(responses);

				DiscordClient client = new DiscordClient(
					new HttpClient(Handler),
					options,
					new DiscordRateLimiter(NullLogger.Instance, new FakeDiscordClock()),
					NullLogger<DiscordClient>.Instance);

				Processor = new DiscordNotificationProcessor(
					client,
					channels,
					new DiscordUserResolver(pluginConfig, NullLogger<DiscordUserResolver>.Instance),
					new DiscordRepeatFilter(new FakeDiscordClock()),
					options,
					buildServerConfig,
					new StaticOptionsMonitor<BuildConfig>(new BuildConfig()),
					users ?? new FakeUserCollection(),
					new FakeServerInfo(),
					NullLogger<DiscordNotificationProcessor>.Instance);
			}

			public RecordingHttpHandler Handler { get; }

			public DiscordNotificationProcessor Processor { get; }

			/// <summary>
			/// The channel map every test starts from, optionally with people in it.
			/// </summary>
			public static DiscordConfig Mapped(params (string Email, string DiscordId)[] users)
			{
				DiscordConfig config = new DiscordConfig
				{
					Guilds = { ["studio"] = GuildId },
					Channels = { [WorkflowSlackId] = new DiscordChannelMapping { Label = "horde-triage", Channel = WorkflowChannel } },
				};

				foreach ((string email, string discordId) in users)
				{
					config.UserMap[email] = discordId;
				}

				return PostLoad(config);
			}

			public static DiscordConfig PostLoad(DiscordConfig config)
			{
				config.PostLoad(new PluginConfigOptions(ConfigVersion.Latest, [], new Acls.AclConfig(), NullLogger.Instance));
				return config;
			}
		}
	}
}
