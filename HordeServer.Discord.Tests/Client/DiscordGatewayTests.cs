// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json;
using HordeServer.Discord.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HordeServer.Discord.Tests.Client
{
	/// <summary>
	/// Tests for the gateway state machine, driven through a scripted socket.
	/// </summary>
	/// <remarks>
	/// Every case here is one a live connection cannot be asked for on demand. The reconnect behaviour is the whole
	/// value of this class - anyone can open a websocket once - and it is only exercised by things going wrong.
	/// </remarks>
	[TestClass]
	public sealed class DiscordGatewayTests
	{
		const string BotToken = "bot-token";
		const string GatewayUrl = "wss://gateway.discord.gg";

		#region Close code policy

		[TestMethod]
		[DataRow(4004, "authentication failed - the bot token is wrong")]
		[DataRow(4010, "invalid shard")]
		[DataRow(4011, "sharding required")]
		[DataRow(4012, "invalid API version")]
		[DataRow(4013, "invalid intents")]
		[DataRow(4014, "disallowed intents - a privileged intent not granted in the portal")]
		public void ConfigurationMistakesAreNotRetried(int closeCode, string why)
			=> Assert.AreEqual(DiscordGatewayRecovery.Fatal, DiscordGatewayPolicy.Classify(closeCode), why);

		[TestMethod]
		[DataRow(4007, "the sequence we would resume from is one the server has forgotten")]
		[DataRow(4009, "the session timed out, so there is nothing left to replay")]
		public void ADeadSessionIsReplacedRatherThanResumed(int closeCode, string why)
			=> Assert.AreEqual(DiscordGatewayRecovery.Reidentify, DiscordGatewayPolicy.Classify(closeCode), why);

		[TestMethod]
		[DataRow(4000)]
		[DataRow(4001)]
		[DataRow(4002)]
		[DataRow(4003)]
		[DataRow(4005)]
		[DataRow(4008)]
		[DataRow(1006)]
		public void EverythingElseIsResumed(int closeCode)
			=> Assert.AreEqual(DiscordGatewayRecovery.Resume, DiscordGatewayPolicy.Classify(closeCode));

		[TestMethod]
		public void ASocketThatDroppedWithNoCloseCodeIsResumed()
			=> Assert.AreEqual(DiscordGatewayRecovery.Resume, DiscordGatewayPolicy.Classify(null),
				"A lost network produces no close code at all, and is the single most common reason to reconnect.");

		#endregion

		#region Backoff

		[TestMethod]
		public void BackoffGrows()
		{
			TimeSpan first = DiscordGatewayPolicy.BackoffFor(0, 1.0);
			TimeSpan second = DiscordGatewayPolicy.BackoffFor(1, 1.0);
			TimeSpan third = DiscordGatewayPolicy.BackoffFor(2, 1.0);

			Assert.IsTrue(second > first, $"{second} should exceed {first}.");
			Assert.IsTrue(third > second, $"{third} should exceed {second}.");
		}

		[TestMethod]
		public void BackoffIsCapped()
		{
			// An outage lasting all weekend must not produce a delay measured in years.
			Assert.AreEqual(DiscordGatewayPolicy.MaximumBackoff, DiscordGatewayPolicy.BackoffFor(1000, 1.0));
		}

		[TestMethod]
		public void BackoffIsNeverZero()
		{
			// A gateway refusing connections instantly, with the unluckiest possible jitter, must not become a spin
			// loop against Discord's front door.
			Assert.IsTrue(DiscordGatewayPolicy.BackoffFor(0, 0.0) > TimeSpan.Zero);
		}

		[TestMethod]
		public void JitterOnlyEverShortens()
		{
			TimeSpan unlucky = DiscordGatewayPolicy.BackoffFor(3, 0.0);
			TimeSpan lucky = DiscordGatewayPolicy.BackoffFor(3, 1.0);

			Assert.IsTrue(unlucky >= lucky / 2, "Jitter should halve the delay at most.");
			Assert.IsTrue(lucky <= DiscordGatewayPolicy.MaximumBackoff);
		}

		[TestMethod]
		public void TheFirstHeartbeatIsOffsetWithinTheInterval()
		{
			TimeSpan interval = TimeSpan.FromSeconds(40.0);

			Assert.AreEqual(TimeSpan.Zero, DiscordGatewayPolicy.FirstHeartbeatDelay(interval, 0.0));
			Assert.AreEqual(interval, DiscordGatewayPolicy.FirstHeartbeatDelay(interval, 1.0));
			Assert.AreEqual(interval / 2, DiscordGatewayPolicy.FirstHeartbeatDelay(interval, 0.5),
				"Discord asks for the first beat to land at a random point inside the interval, not after it.");
		}

		#endregion

		#region Identify

		[TestMethod]
		public async Task AFreshConnectionIdentifies()
		{
			Harness harness = new Harness();
			FakeDiscordWebSocket socket = harness.NextSocket();

			socket.EnqueueHello();
			socket.EnqueueReady();
			socket.EnqueueClose(null);

			await harness.Gateway.RunSessionAsync(default);

			JsonElement identify = socket.SentWithOpcode(DiscordGatewayOpcode.Identify)
				?? throw new AssertFailedException("No IDENTIFY was sent.");

			Assert.AreEqual(BotToken, identify.GetProperty("d").GetProperty("token").GetString());
			Assert.AreEqual(0, identify.GetProperty("d").GetProperty("intents").GetInt32(),
				"Interactions are delivered regardless of intents, and asking for one that is privileged would "
				+ "require the application to be verified.");
		}

		[TestMethod]
		public async Task TheGatewayUrlCarriesTheVersionAndEncoding()
		{
			Harness harness = new Harness();
			FakeDiscordWebSocket socket = harness.NextSocket();

			socket.EnqueueHello();
			socket.EnqueueClose(null);

			await harness.Gateway.RunSessionAsync(default);

			StringAssert.Contains(socket.ConnectedTo?.ToString(), $"v={DiscordGateway.GatewayVersion}");
			StringAssert.Contains(socket.ConnectedTo?.ToString(), "encoding=json");
		}

		[TestMethod]
		public async Task ReadyEstablishesTheSession()
		{
			Harness harness = new Harness();
			FakeDiscordWebSocket socket = harness.NextSocket();

			socket.EnqueueHello();
			socket.EnqueueReady(sessionId: "abc");
			socket.EnqueueClose(null);

			(_, bool established) = await harness.Gateway.RunSessionAsync(default);

			Assert.IsTrue(established);
			Assert.AreEqual("999", harness.Gateway.BotUserId);
			Assert.AreEqual("Horde", harness.Gateway.BotUsername);
		}

		[TestMethod]
		public async Task AConnectionThatNeverReachedReadyIsNotEstablished()
		{
			Harness harness = new Harness();
			FakeDiscordWebSocket socket = harness.NextSocket();

			socket.EnqueueHello();
			socket.EnqueueClose(4000);

			(_, bool established) = await harness.Gateway.RunSessionAsync(default);

			Assert.IsFalse(established,
				"The distinction drives the backoff: a session that worked and dropped should reconnect promptly, "
				+ "one that never worked should not.");
		}

		#endregion

		#region Resume

		[TestMethod]
		public async Task TheSecondConnectionResumesRatherThanIdentifying()
		{
			Harness harness = new Harness();

			FakeDiscordWebSocket first = harness.NextSocket();
			first.EnqueueHello();
			first.EnqueueReady(sessionId: "abc", resumeUrl: "wss://resume.example.com", sequence: 7);
			first.EnqueueClose(null);

			await harness.Gateway.RunSessionAsync(default);

			FakeDiscordWebSocket second = harness.NextSocket();
			second.EnqueueHello();
			second.EnqueueJson("""{"op":0,"s":8,"t":"RESUMED","d":{}}""");
			second.EnqueueClose(null);

			await harness.Gateway.RunSessionAsync(default);

			JsonElement resume = second.SentWithOpcode(DiscordGatewayOpcode.Resume)
				?? throw new AssertFailedException("No RESUME was sent.");

			Assert.AreEqual("abc", resume.GetProperty("d").GetProperty("session_id").GetString());
			Assert.AreEqual(7, resume.GetProperty("d").GetProperty("seq").GetInt32());
			Assert.IsNull(second.SentWithOpcode(DiscordGatewayOpcode.Identify));

			StringAssert.StartsWith(second.ConnectedTo?.ToString(), "wss://resume.example.com",
				"A resume goes to the URL READY supplied, not back to the one /gateway/bot returned.");
		}

		[TestMethod]
		public async Task AResumeDoesNotAskWhereTheGatewayIs()
		{
			Harness harness = new Harness();

			FakeDiscordWebSocket first = harness.NextSocket();
			first.EnqueueHello();
			first.EnqueueReady();
			first.EnqueueClose(null);

			await harness.Gateway.RunSessionAsync(default);
			int afterFirst = harness.Handler.Requests.Count;

			FakeDiscordWebSocket second = harness.NextSocket();
			second.EnqueueHello();
			second.EnqueueClose(null);

			await harness.Gateway.RunSessionAsync(default);

			Assert.AreEqual(afterFirst, harness.Handler.Requests.Count,
				"Resuming reuses resume_gateway_url, so it costs no REST call - which also keeps it off the daily "
				+ "session start limit.");
		}

		[TestMethod]
		public async Task AnUnresumableInvalidSessionStartsOver()
		{
			Harness harness = new Harness();

			FakeDiscordWebSocket first = harness.NextSocket();
			first.EnqueueHello();
			first.EnqueueReady(sessionId: "abc");
			first.EnqueueClose(null);

			await harness.Gateway.RunSessionAsync(default);

			FakeDiscordWebSocket second = harness.NextSocket();

			// A short heartbeat interval on purpose: every delay the heartbeat loop asks for is then well under a
			// second, so a delay of one to five seconds can only be the one under test.
			second.EnqueueHello(heartbeatIntervalMs: 200);
			second.EnqueueJson($$"""{"op":{{DiscordGatewayOpcode.InvalidSession}},"d":false}""");

			Task<(DiscordGatewayRecovery Recovery, bool Established)> run = harness.Gateway.RunSessionAsync(default);

			await harness.Clock.ReleaseUntilAsync(run);
			(DiscordGatewayRecovery recovery, _) = await run;

			Assert.AreEqual(DiscordGatewayRecovery.Reidentify, recovery);

			// Discord asks for a wait of one to five seconds before identifying again, to spread out the reconnect
			// storm after a gateway restart.
			Assert.IsTrue(
				harness.Clock.Requested.Any(x => x >= TimeSpan.FromSeconds(1.0) && x <= TimeSpan.FromSeconds(5.0)),
				$"Delays asked for were {String.Join(", ", harness.Clock.Requested)}.");

			FakeDiscordWebSocket third = harness.NextSocket();
			third.EnqueueHello();
			third.EnqueueClose(null);

			await harness.Gateway.RunSessionAsync(default);

			Assert.IsNotNull(third.SentWithOpcode(DiscordGatewayOpcode.Identify),
				"The session was thrown away, so the next connection must identify rather than resume.");
		}

		[TestMethod]
		public async Task AResumableInvalidSessionKeepsTheSession()
		{
			Harness harness = new Harness();

			FakeDiscordWebSocket first = harness.NextSocket();
			first.EnqueueHello();
			first.EnqueueReady(sessionId: "abc");
			first.EnqueueClose(null);

			await harness.Gateway.RunSessionAsync(default);

			FakeDiscordWebSocket second = harness.NextSocket();
			second.EnqueueHello();
			second.EnqueueJson($$"""{"op":{{DiscordGatewayOpcode.InvalidSession}},"d":true}""");

			(DiscordGatewayRecovery recovery, _) = await harness.Gateway.RunSessionAsync(default);

			Assert.AreEqual(DiscordGatewayRecovery.Resume, recovery);
			Assert.AreEqual(DiscordGateway.ResumableCloseCode, second.ClosedWith);
		}

		[TestMethod]
		public async Task ReconnectHangsUpWithACodeThatPreservesTheSession()
		{
			Harness harness = new Harness();
			FakeDiscordWebSocket socket = harness.NextSocket();

			socket.EnqueueHello();
			socket.EnqueueReady();
			socket.EnqueueJson($$"""{"op":{{DiscordGatewayOpcode.Reconnect}},"d":null}""");

			(DiscordGatewayRecovery recovery, _) = await harness.Gateway.RunSessionAsync(default);

			Assert.AreEqual(DiscordGatewayRecovery.Resume, recovery);
			Assert.AreEqual(DiscordGateway.ResumableCloseCode, socket.ClosedWith);
			Assert.AreNotEqual(1000, socket.ClosedWith,
				"Closing cleanly tells Discord to discard the session, which would silently turn every reconnect "
				+ "into a lost one.");
		}

		#endregion

		#region Heartbeat

		[TestMethod]
		public async Task HeartbeatsCarryTheLastSequenceSeen()
		{
			Harness harness = new Harness();
			FakeDiscordWebSocket socket = harness.NextSocket();

			socket.EnqueueHello();
			socket.EnqueueReady(sequence: 12);

			Task<(DiscordGatewayRecovery Recovery, bool Established)> run = harness.Gateway.RunSessionAsync(default);

			// The offset before the first beat, then the beat itself.
			await harness.Clock.ReleaseNextAsync();
			await WaitForAsync(() => socket.CountWithOpcode(DiscordGatewayOpcode.Heartbeat) > 0);

			JsonElement heartbeat = socket.SentWithOpcode(DiscordGatewayOpcode.Heartbeat)!.Value;

			Assert.AreEqual(12, heartbeat.GetProperty("d").GetInt32(),
				"Resuming from the wrong sequence replays the wrong events, so the sequence has to track dispatches.");

			socket.EnqueueClose(null);
			await run;
		}

		[TestMethod]
		public async Task AConnectionThatStopsAcknowledgingIsAbandoned()
		{
			Harness harness = new Harness();
			FakeDiscordWebSocket socket = harness.NextSocket();

			socket.AcknowledgeHeartbeats = false;
			socket.EnqueueHello();
			socket.EnqueueReady();

			Task<(DiscordGatewayRecovery Recovery, bool Established)> run = harness.Gateway.RunSessionAsync(default);

			await harness.Clock.ReleaseNextAsync();   // the initial offset - a heartbeat goes out
			await harness.Clock.ReleaseNextAsync();   // a full interval later, still unacknowledged

			(DiscordGatewayRecovery recovery, bool established) = await run;

			Assert.AreEqual(DiscordGatewayRecovery.Resume, recovery);
			Assert.IsTrue(established);
			Assert.AreEqual(DiscordGateway.ResumableCloseCode, socket.ClosedWith,
				"A zombied socket is often still open at the TCP level. Nothing but the missing acknowledgement "
				+ "says it is useless, and the session behind it is still good.");
		}

		[TestMethod]
		public async Task AnAcknowledgedHeartbeatKeepsTheConnection()
		{
			Harness harness = new Harness();
			FakeDiscordWebSocket socket = harness.NextSocket();

			socket.EnqueueHello();
			socket.EnqueueReady();

			Task<(DiscordGatewayRecovery Recovery, bool Established)> run = harness.Gateway.RunSessionAsync(default);

			await harness.Clock.ReleaseNextAsync();
			await WaitForAsync(() => socket.CountWithOpcode(DiscordGatewayOpcode.Heartbeat) >= 1);

			// The acknowledgement is queued by the fake as soon as the beat is sent, so by the time the next interval
			// elapses it has been read and the connection is not a zombie.
			await harness.Clock.ReleaseNextAsync();
			await WaitForAsync(() => socket.CountWithOpcode(DiscordGatewayOpcode.Heartbeat) >= 2);

			Assert.IsNull(socket.ClosedWith, "The connection was answering, so nothing should have hung up on it.");

			socket.EnqueueClose(null);
			await run;
		}

		[TestMethod]
		public async Task TheServerCanAskForAHeartbeatEarly()
		{
			Harness harness = new Harness();
			FakeDiscordWebSocket socket = harness.NextSocket();

			socket.EnqueueHello();
			socket.EnqueueReady();
			socket.EnqueueJson($$"""{"op":{{DiscordGatewayOpcode.Heartbeat}},"d":null}""");
			socket.EnqueueClose(null);

			await harness.Gateway.RunSessionAsync(default);

			Assert.IsTrue(socket.CountWithOpcode(DiscordGatewayOpcode.Heartbeat) >= 1,
				"Opcode 1 arriving from the server is a request for an immediate beat, not an acknowledgement.");
		}

		#endregion

		#region Dispatch

		[TestMethod]
		public async Task InteractionsAreHandedToTheListener()
		{
			Harness harness = new Harness();
			FakeDiscordWebSocket socket = harness.NextSocket();

			List<DiscordGatewayDispatch> received = new List<DiscordGatewayDispatch>();
			harness.Gateway.DispatchReceived += received.Add;

			socket.EnqueueHello();
			socket.EnqueueReady();
			socket.EnqueueJson("""{"op":0,"s":2,"t":"INTERACTION_CREATE","d":{"id":"77","type":3}}""");
			socket.EnqueueClose(null);

			await harness.Gateway.RunSessionAsync(default);

			Assert.AreEqual(1, received.Count);
			Assert.AreEqual("INTERACTION_CREATE", received[0].EventName);
			Assert.AreEqual("77", received[0].Data.GetProperty("id").GetString());
		}

		[TestMethod]
		public async Task ConnectionManagementIsNotReportedAsAnEvent()
		{
			Harness harness = new Harness();
			FakeDiscordWebSocket socket = harness.NextSocket();

			List<string> received = new List<string>();
			harness.Gateway.DispatchReceived += x => received.Add(x.EventName);

			socket.EnqueueHello();
			socket.EnqueueReady();
			socket.EnqueueJson("""{"op":0,"s":2,"t":"RESUMED","d":{}}""");
			socket.EnqueueClose(null);

			await harness.Gateway.RunSessionAsync(default);

			CollectionAssert.AreEqual(Array.Empty<string>(), received,
				"READY and RESUMED are the gateway's own business; a listener wants interactions.");
		}

		[TestMethod]
		public async Task AFrameThatIsNotJsonDoesNotEndTheConnection()
		{
			Harness harness = new Harness();
			FakeDiscordWebSocket socket = harness.NextSocket();

			socket.EnqueueHello();
			socket.EnqueueJson("this is not json");
			socket.EnqueueReady();
			socket.EnqueueClose(null);

			(_, bool established) = await harness.Gateway.RunSessionAsync(default);

			Assert.IsTrue(established, "One unreadable frame is not a reason to drop a working session.");
		}

		#endregion

		#region Enablement

		[TestMethod]
		public async Task TheGatewayStaysShutWhenInteractionsAreOff()
		{
			Harness harness = new Harness(enableInteractions: false);

			Assert.IsFalse(harness.Gateway.IsEnabled);

			await harness.Gateway.StartAsync(default);

			Assert.AreEqual(0, harness.Handler.Requests.Count,
				"Posting notifications works with the gateway off, and that is a supported configuration.");
		}

		[TestMethod]
		public void TheGatewayStaysShutWithoutAToken()
			=> Assert.IsFalse(new Harness(botToken: null).Gateway.IsEnabled);

		#endregion

		/// <summary>
		/// Polls a condition rather than sleeping on it, for the handful of places where work is handed to another task.
		/// </summary>
		static async Task WaitForAsync(Func<bool> condition)
		{
			DateTime deadline = DateTime.UtcNow + GatedDiscordClock.Patience;

			while (DateTime.UtcNow < deadline)
			{
				if (condition())
				{
					return;
				}

				await Task.Yield();
			}

			Assert.Fail("Timed out waiting for the gateway.");
		}

		/// <summary>
		/// A gateway wired to a scripted socket and a clock the test drives.
		/// </summary>
		sealed class Harness
		{
			readonly Queue<FakeDiscordWebSocket> _sockets = new Queue<FakeDiscordWebSocket>();

			public Harness(bool enableInteractions = true, string? botToken = BotToken)
			{
				DiscordServerConfig serverConfig = new DiscordServerConfig
				{
					BotToken = botToken,
					GuildId = "1",
					EnableInteractions = enableInteractions,
				};

				// One canned answer per fresh session. Resumes do not ask, so a test that only resumes never reaches
				// the end of this queue.
				Handler = new RecordingHttpHandler(
					GatewayResponse(), GatewayResponse(), GatewayResponse(), GatewayResponse());

				DiscordClient client = new DiscordClient(
					new HttpClient(Handler) { BaseAddress = new Uri(DiscordClient.ApiBaseUrl) },
					Options.Create(serverConfig),
					new DiscordRateLimiter(NullLogger.Instance, Clock),
					NullLogger<DiscordClient>.Instance);

				Gateway = new DiscordGateway(
					Options.Create(serverConfig),
					client,
					NullLogger<DiscordGateway>.Instance,
					() => _sockets.Dequeue(),
					Clock);
			}

			public GatedDiscordClock Clock { get; } = new GatedDiscordClock();

			public RecordingHttpHandler Handler { get; }

			public DiscordGateway Gateway { get; }

			/// <summary>
			/// Queues the socket the next connection attempt will be handed.
			/// </summary>
			public FakeDiscordWebSocket NextSocket()
			{
				FakeDiscordWebSocket socket = new FakeDiscordWebSocket();
				_sockets.Enqueue(socket);
				return socket;
			}

			static HttpResponseMessage GatewayResponse()
				=> RecordingHttpHandler.Json(HttpStatusCode.OK,
					$$$"""{"url":"{{{GatewayUrl}}}","shards":1,"session_start_limit":{"remaining":999,"reset_after":86400000} }""");
		}
	}
}
