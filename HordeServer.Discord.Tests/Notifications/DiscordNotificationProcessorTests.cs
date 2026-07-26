// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Text.Json;
using EpicGames.Horde.Agents;
using EpicGames.Horde.Users;
using HordeServer.Agents;
using HordeServer.Configuration;
using HordeServer.Devices;
using HordeServer.Discord.Client;
using HordeServer.Discord.Notifications;
using HordeServer.Discord.Tests.Client;
using HordeServer.Plugins;
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
	/// It proves what would be sent, not what Discord does with it. No message from this plugin has ever been posted
	/// to a real server.
	/// </remarks>
	[TestClass]
	public sealed class DiscordNotificationProcessorTests
	{
		const string ConfigChannel = "100000000000000001";
		const string AgentChannel = "100000000000000002";
		const string DeviceChannel = "100000000000000003";
		const string UpdateStreamsChannel = "100000000000000004";
		const string WorkflowChannel = "100000000000000005";

		const string WorkflowSlackId = "C0832ESJUR5";

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
			StringAssert.Contains(harness.Handler.Message(0).GetProperty("content").GetString(), "For Ada Lovelace");
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
			StringAssert.Contains(harness.Handler.Message(0).GetProperty("content").GetString(), "For Ada Lovelace",
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

			Assert.AreEqual("For Ada Lovelace", harness.Handler.Message(0).GetProperty("content").GetString(),
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
			Harness harness = new Harness(channels: new DiscordConfig(), agentChannel: null);

			await harness.Processor.SendSessionConflictReportAsync([(new AgentId("build-07"), 42)], default);

			Assert.AreEqual(0, harness.Handler.Requests.Count);
		}

		#endregion

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
			public Harness(FakeUserCollection? users = null, DiscordConfig? channels = null, string? botToken = "token", string? agentChannel = AgentChannel)
			{
				DiscordServerConfig serverConfig = new DiscordServerConfig
				{
					BotToken = botToken,
					ConfigNotificationChannel = ConfigChannel,
					AgentNotificationChannel = agentChannel,
					DeviceNotificationChannel = DeviceChannel,
					UpdateStreamsNotificationChannel = UpdateStreamsChannel,

					// Plain text, so an assertion on a message reads as the message rather than as an emoji shortcode.
					ErrorPrefix = String.Empty,
					WarningPrefix = String.Empty,
				};

				IOptions<DiscordServerConfig> options = Options.Create(serverConfig);

				DiscordChannelResolver resolver = new DiscordChannelResolver(
					new StaticOptionsMonitor<DiscordConfig>(channels ?? MappedChannels()),
					options,
					Options.Create(new BuildServerConfig()),
					NullLogger<DiscordChannelResolver>.Instance);

				Handler = new RecordingHttpHandler();

				DiscordClient client = new DiscordClient(
					new HttpClient(Handler),
					options,
					new DiscordRateLimiter(NullLogger.Instance, new FakeDiscordClock()),
					NullLogger<DiscordClient>.Instance);

				Processor = new DiscordNotificationProcessor(
					client,
					resolver,
					new DiscordRepeatFilter(new FakeDiscordClock()),
					options,
					new StaticOptionsMonitor<BuildConfig>(new BuildConfig()),
					users ?? new FakeUserCollection(),
					new FakeServerInfo(),
					NullLogger<DiscordNotificationProcessor>.Instance);
			}

			public RecordingHttpHandler Handler { get; }

			public DiscordNotificationProcessor Processor { get; }

			static DiscordConfig MappedChannels()
			{
				DiscordConfig config = new DiscordConfig
				{
					Channels = { [WorkflowSlackId] = new DiscordChannelMapping { Label = "horde-triage", Channel = WorkflowChannel } },
				};

				config.PostLoad(new PluginConfigOptions(ConfigVersion.Latest, [], new Acls.AclConfig(), NullLogger.Instance));
				return config;
			}
		}
	}
}
