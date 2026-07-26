// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HordeServer.Discord.Client
{
	/// <summary>
	/// Keeps a websocket open to Discord's gateway, so the plugin can be told about interactions.
	/// </summary>
	/// <remarks>
	/// Notifications do not need this - they are posted over REST and work with the gateway switched off entirely.
	/// What needs it is everything that comes *back*: buttons, modals and slash commands are delivered as gateway
	/// events, and Discord's only alternative is an HTTP interactions endpoint, which would require a publicly
	/// reachable URL with a verifiable TLS certificate. A build server is usually not that, so the socket it is.
	///
	/// The connection is expected to break. Discord asks clients to reconnect on request, drops them on deploys, and
	/// the state machine here exists to make that uninteresting: a session survives a reconnect through
	/// <c>RESUME</c>, and events sent while the socket was down are replayed. Only the codes in
	/// <see cref="DiscordGatewayPolicy.Classify"/> stop it trying.
	/// </remarks>
	public sealed class DiscordGateway : IHostedService, IDisposable
	{
		/// <summary>
		/// Gateway API version, pinned for the same reason the REST base URL is.
		/// </summary>
		public const int GatewayVersion = 10;

		/// <summary>
		/// Gateway intents requested at identify.
		/// </summary>
		/// <remarks>
		/// **Zero, deliberately.** Intents subscribe a bot to categories of guild event - messages, members,
		/// presences - and this plugin wants none of them: it posts, and it is told when someone presses one of its
		/// own buttons. <c>INTERACTION_CREATE</c> is not gated by intents and arrives regardless.
		///
		/// Worth stating because the alternative is expensive. Asking for a privileged intent such as
		/// <c>GUILD_MEMBERS</c> means enabling it in the developer portal, and once the application is in more than
		/// 100 guilds it means Discord verifying the application. Requesting nothing keeps the bot installable by
		/// anyone with Manage Server and makes <c>4014 Disallowed intents</c> impossible.
		/// </remarks>
		public const int Intents = 0;

		/// <summary>
		/// Close code used when hanging up on a connection we intend to resume.
		/// </summary>
		/// <remarks>
		/// Not <c>1000</c>. A clean close tells Discord the session is finished and it discards the state a resume
		/// would replay from, so closing politely is how you silently turn every reconnect into a lost session.
		/// </remarks>
		public const int ResumableCloseCode = 4000;

		readonly IOptions<DiscordServerConfig> _serverConfig;
		readonly DiscordClient _client;
		readonly Func<IDiscordWebSocket> _socketFactory;
		readonly IDiscordClock _clock;
		readonly ILogger _logger;

		readonly CancellationTokenSource _stopping = new CancellationTokenSource();
		Task? _running;

		// Session state, which is what a resume replays from and what a re-identify throws away.
		string? _sessionId;
		string? _resumeGatewayUrl;
		int _sequence;

		/// <summary>
		/// Raised for every dispatch that is not part of connection management.
		/// </summary>
		/// <remarks>
		/// Synchronous on the receive loop, so a handler that blocks stops the gateway reading - including its own
		/// heartbeat acknowledgements, which will eventually be diagnosed as a dead connection. Handlers must return
		/// promptly. The interaction handler in this plugin acknowledges within Discord's three-second deadline and
		/// does the work afterwards, which is the shape to copy.
		/// </remarks>
		public event Action<DiscordGatewayDispatch>? DispatchReceived;

		/// <summary>
		/// Snowflake of the bot account, learned from <c>READY</c>. Null until the first successful identify.
		/// </summary>
		public string? BotUserId { get; private set; }

		/// <summary>
		/// Username of the bot account, learned from <c>READY</c>.
		/// </summary>
		public string? BotUsername { get; private set; }

		/// <summary>
		/// Whether a session is currently established.
		/// </summary>
		public bool IsConnected { get; private set; }

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="serverConfig">Server configuration, for the bot token and the interactions switch.</param>
		/// <param name="client">REST client, used once per session to look up the gateway URL.</param>
		/// <param name="logger">Logger for connection lifecycle.</param>
		/// <param name="socketFactory">Creates a socket per connection attempt. Defaults to a real one.</param>
		/// <param name="clock">Clock to wait against. Defaults to the system clock.</param>
		public DiscordGateway(
			IOptions<DiscordServerConfig> serverConfig,
			DiscordClient client,
			ILogger<DiscordGateway> logger,
			Func<IDiscordWebSocket>? socketFactory = null,
			IDiscordClock? clock = null)
		{
			_serverConfig = serverConfig;
			_client = client;
			_logger = logger;
			_socketFactory = socketFactory ?? (static () => new DiscordClientWebSocket());
			_clock = clock ?? DiscordSystemClock.Instance;
		}

		/// <summary>
		/// Whether the gateway is configured to run at all.
		/// </summary>
		public bool IsEnabled => _serverConfig.Value.IsConfigured && _serverConfig.Value.EnableInteractions;

		/// <inheritdoc/>
		public Task StartAsync(CancellationToken cancellationToken)
		{
			if (!IsEnabled)
			{
				// Not a warning. Running without interactions is a supported configuration - it is what every
				// deployment looks like before anyone has set up issue triage.
				_logger.LogInformation("Discord gateway not started ({Reason})",
					_serverConfig.Value.IsConfigured ? "EnableInteractions is false" : "no bot token configured");

				return Task.CompletedTask;
			}

			// Task.Run rather than awaiting: StartAsync runs in the server's startup path and every hosted service
			// after this one waits on it returning.
			_running = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);

			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public async Task StopAsync(CancellationToken cancellationToken)
		{
			await _stopping.CancelAsync();

			if (_running != null)
			{
				// The socket read is what is being cancelled, so this returns promptly. WaitAsync guards against a
				// wedged read holding up shutdown regardless.
				try
				{
					await _running.WaitAsync(cancellationToken);
				}
				catch (OperationCanceledException)
				{
				}
			}
		}

		/// <inheritdoc/>
		public void Dispose()
		{
			_stopping.Dispose();
		}

		/// <summary>
		/// Connects, and keeps reconnecting until told to stop.
		/// </summary>
		/// <param name="cancellationToken">Cancellation token that ends the loop.</param>
		public async Task RunAsync(CancellationToken cancellationToken)
		{
			int attempt = 0;

			while (!cancellationToken.IsCancellationRequested)
			{
				DiscordGatewayRecovery recovery;
				bool established = false;

				try
				{
					(recovery, established) = await RunSessionAsync(cancellationToken);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					break;
				}
				catch (Exception ex)
				{
					// Anything thrown out of a session - a socket that could not connect, a DNS failure, malformed
					// JSON - is a reason to reconnect rather than to stop. The one thing that must not happen is
					// this loop ending silently and interactions never working again.
					_logger.LogWarning(ex, "Discord gateway session ended with an error");
					recovery = DiscordGatewayRecovery.Resume;
				}

				IsConnected = false;

				if (recovery == DiscordGatewayRecovery.Fatal)
				{
					_logger.LogError("Discord gateway will not reconnect. Notifications still post over REST, but "
						+ "buttons, modals and slash commands are unavailable until the server is restarted with a "
						+ "corrected configuration.");
					return;
				}

				if (recovery == DiscordGatewayRecovery.Reidentify)
				{
					ForgetSession();
				}

				if (established)
				{
					// A session that got as far as READY and then dropped is an ordinary reconnect, not a failure to
					// connect, so it should not inherit the backoff of whatever came before it.
					attempt = 0;
				}

				TimeSpan delay = DiscordGatewayPolicy.BackoffFor(attempt, Random.Shared.NextDouble());
				attempt++;

				_logger.LogInformation("Discord gateway reconnecting in {Delay:0.0}s ({Recovery})",
					delay.TotalSeconds, recovery);

				try
				{
					await _clock.DelayAsync(delay, cancellationToken);
				}
				catch (OperationCanceledException)
				{
					break;
				}
			}
		}

		/// <summary>
		/// Runs one connection, from opening the socket to whatever ends it.
		/// </summary>
		/// <param name="cancellationToken">Cancellation token that ends the session.</param>
		/// <returns>What to do with the session, and whether this connection ever became usable.</returns>
		public async Task<(DiscordGatewayRecovery Recovery, bool Established)> RunSessionAsync(CancellationToken cancellationToken)
		{
			bool resuming = _sessionId != null && _resumeGatewayUrl != null;
			string? baseUrl = resuming ? _resumeGatewayUrl : await _client.GetGatewayUrlAsync(cancellationToken);

			if (baseUrl == null)
			{
				// Could not even ask where the gateway is. Almost always the token or the network, both of which are
				// worth retrying.
				return (DiscordGatewayRecovery.Resume, false);
			}

			using IDiscordWebSocket socket = _socketFactory();
			using CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

			await socket.ConnectAsync(BuildUri(baseUrl), session.Token);

			SessionState state = new SessionState();
			Task? heartbeat = null;

			try
			{
				while (true)
				{
					DiscordWebSocketFrame frame;

					try
					{
						frame = await socket.ReceiveAsync(session.Token);
					}
					catch (OperationCanceledException) when (state.Zombied)
					{
						// The heartbeat loop gave up on this connection and cancelled the read. The session itself is
						// still good, so reconnect and replay it.
						_logger.LogWarning("Discord gateway stopped acknowledging heartbeats; reconnecting");
						return (DiscordGatewayRecovery.Resume, state.Established);
					}

					if (frame.IsClose)
					{
						DiscordGatewayRecovery recovery = DiscordGatewayPolicy.Classify(frame.CloseStatus);

						_logger.Log(recovery == DiscordGatewayRecovery.Fatal ? LogLevel.Error : LogLevel.Information,
							"Discord gateway closed: {CloseStatus} {CloseDescription} ({Recovery})",
							frame.CloseStatus, frame.CloseDescription ?? "<no reason>", recovery);

						return (recovery, state.Established);
					}

					DiscordGatewayFrame? payload = Parse(frame.Text!);

					if (payload == null)
					{
						continue;
					}

					if (payload.Sequence != null)
					{
						_sequence = payload.Sequence.Value;
					}

					switch (payload.Op)
					{
						case DiscordGatewayOpcode.Hello:
							state.HeartbeatInterval = ReadHeartbeatInterval(payload.Data);
							heartbeat = Task.Run(() => HeartbeatAsync(socket, state, session, session.Token), CancellationToken.None);
							await (resuming
								? SendResumeAsync(socket, state, session.Token)
								: SendIdentifyAsync(socket, state, session.Token));
							break;

						case DiscordGatewayOpcode.Heartbeat:
							// The server can ask for one out of band, and expects it immediately.
							await SendHeartbeatAsync(socket, state, session.Token);
							break;

						case DiscordGatewayOpcode.HeartbeatAck:
							state.AckPending = false;
							break;

						case DiscordGatewayOpcode.Reconnect:
							_logger.LogInformation("Discord gateway asked us to reconnect");
							await CloseQuietlyAsync(socket, ResumableCloseCode, "reconnecting");
							return (DiscordGatewayRecovery.Resume, state.Established);

						case DiscordGatewayOpcode.InvalidSession:
							return (await HandleInvalidSessionAsync(socket, payload.Data, state, cancellationToken), state.Established);

						case DiscordGatewayOpcode.Dispatch:
							HandleDispatch(payload, state);
							break;

						default:
							_logger.LogDebug("Discord gateway sent opcode {Opcode}, which this client ignores", payload.Op);
							break;
					}
				}
			}
			finally
			{
				IsConnected = false;

				await session.CancelAsync();

				if (heartbeat != null)
				{
					// Awaited rather than abandoned: it holds the send gate, and a heartbeat arriving on a socket the
					// next session has already replaced is a confusing thing to debug.
					try
					{
						await heartbeat;
					}
					catch (OperationCanceledException)
					{
					}
				}
			}
		}

		void HandleDispatch(DiscordGatewayFrame payload, SessionState state)
		{
			switch (payload.EventName)
			{
				case "READY":
					_sessionId = ReadString(payload.Data, "session_id");
					_resumeGatewayUrl = ReadString(payload.Data, "resume_gateway_url");

					if (payload.Data.ValueKind == JsonValueKind.Object && payload.Data.TryGetProperty("user", out JsonElement user))
					{
						BotUserId = ReadString(user, "id");
						BotUsername = ReadString(user, "username");
					}

					state.Established = true;
					IsConnected = true;

					_logger.LogInformation("Discord gateway ready as {Username} ({UserId}), session {SessionId}",
						BotUsername ?? "<unknown>", BotUserId ?? "<unknown>", _sessionId ?? "<none>");
					break;

				case "RESUMED":
					state.Established = true;
					IsConnected = true;

					_logger.LogInformation("Discord gateway resumed session {SessionId} at sequence {Sequence}",
						_sessionId ?? "<none>", _sequence);
					break;

				default:
					if (payload.EventName != null)
					{
						DispatchReceived?.Invoke(new DiscordGatewayDispatch(payload.EventName, payload.Data));
					}
					break;
			}
		}

		async Task<DiscordGatewayRecovery> HandleInvalidSessionAsync(IDiscordWebSocket socket, JsonElement data, SessionState state, CancellationToken cancellationToken)
		{
			// The payload is a bare boolean saying whether a resume is still worth attempting.
			bool resumable = data.ValueKind == JsonValueKind.True;

			_logger.LogInformation("Discord gateway rejected the session (resumable: {Resumable})", resumable);

			await CloseQuietlyAsync(socket, ResumableCloseCode, "invalid session");

			if (resumable)
			{
				return DiscordGatewayRecovery.Resume;
			}

			ForgetSession();

			// Discord asks for a wait of one to five seconds before identifying again, to spread out the reconnect
			// storm after a gateway restart. The caller's backoff starts below a second, so it cannot supply this.
			await _clock.DelayAsync(TimeSpan.FromSeconds(1.0 + (Random.Shared.NextDouble() * 4.0)), cancellationToken);

			return DiscordGatewayRecovery.Reidentify;
		}

		async Task HeartbeatAsync(IDiscordWebSocket socket, SessionState state, CancellationTokenSource session, CancellationToken cancellationToken)
		{
			// Offset the first beat by a random fraction of the interval, so a fleet reconnecting after an outage
			// does not synchronise.
			await _clock.DelayAsync(
				DiscordGatewayPolicy.FirstHeartbeatDelay(state.HeartbeatInterval, Random.Shared.NextDouble()),
				cancellationToken);

			while (!cancellationToken.IsCancellationRequested)
			{
				if (state.AckPending)
				{
					// A beat went unanswered for a whole interval. The socket is very likely still open and utterly
					// useless - the failure mode Discord's docs call a zombied connection - and the only way to find
					// out is to stop trusting it.
					state.Zombied = true;

					await CloseQuietlyAsync(socket, ResumableCloseCode, "heartbeat not acknowledged");
					await session.CancelAsync();

					return;
				}

				await SendHeartbeatAsync(socket, state, cancellationToken);
				await _clock.DelayAsync(state.HeartbeatInterval, cancellationToken);
			}
		}

		async Task SendHeartbeatAsync(IDiscordWebSocket socket, SessionState state, CancellationToken cancellationToken)
		{
			state.AckPending = true;

			// The sequence is sent as null until the first dispatch arrives, which is what Discord expects rather
			// than a zero.
			string sequence = _sequence == 0 ? "null" : _sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);

			await SendAsync(socket, $"{{\"op\":{DiscordGatewayOpcode.Heartbeat},\"d\":{sequence}}}", state, cancellationToken);
		}

		Task SendIdentifyAsync(IDiscordWebSocket socket, SessionState state, CancellationToken cancellationToken)
		{
			object identify = new
			{
				op = DiscordGatewayOpcode.Identify,
				d = new
				{
					token = _serverConfig.Value.BotToken,
					intents = Intents,
					properties = new
					{
						os = Environment.OSVersion.Platform.ToString(),
						browser = "HordeServer.Discord",
						device = "HordeServer.Discord",
					},
				},
			};

			return SendAsync(socket, JsonSerializer.Serialize(identify), state, cancellationToken);
		}

		Task SendResumeAsync(IDiscordWebSocket socket, SessionState state, CancellationToken cancellationToken)
		{
			object resume = new
			{
				op = DiscordGatewayOpcode.Resume,
				d = new
				{
					token = _serverConfig.Value.BotToken,
					session_id = _sessionId,
					seq = _sequence,
				},
			};

			return SendAsync(socket, JsonSerializer.Serialize(resume), state, cancellationToken);
		}

		/// <summary>
		/// Sends on the shared socket, one frame at a time.
		/// </summary>
		/// <remarks>
		/// The heartbeat loop and the receive loop both send, and a websocket permits exactly one send in flight.
		/// </remarks>
		static async Task SendAsync(IDiscordWebSocket socket, string text, SessionState state, CancellationToken cancellationToken)
		{
			await state.SendGate.WaitAsync(cancellationToken);

			try
			{
				await socket.SendAsync(text, cancellationToken);
			}
			finally
			{
				state.SendGate.Release();
			}
		}

		async Task CloseQuietlyAsync(IDiscordWebSocket socket, int closeStatus, string description)
		{
			try
			{
				// Its own timeout, and never the session token: this is called on the way out, usually because the
				// connection is already unhealthy, and a close that hangs must not hold up the reconnect.
				using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5.0));

				await socket.CloseAsync(closeStatus, description, timeout.Token);
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Discord gateway could not be closed cleanly, which is rarely interesting");
			}
		}

		void ForgetSession()
		{
			_sessionId = null;
			_resumeGatewayUrl = null;
			_sequence = 0;
		}

		DiscordGatewayFrame? Parse(string text)
		{
			try
			{
				return JsonSerializer.Deserialize<DiscordGatewayFrame>(text);
			}
			catch (JsonException ex)
			{
				_logger.LogWarning(ex, "Discord gateway sent a frame that could not be parsed");
				return null;
			}
		}

		static Uri BuildUri(string baseUrl)
			=> new Uri($"{baseUrl.TrimEnd('/')}/?v={GatewayVersion}&encoding=json");

		static TimeSpan ReadHeartbeatInterval(JsonElement data)
		{
			// ValueKind first, every time. A frame that arrived without a "d" leaves this as default(JsonElement),
			// whose ValueKind is Undefined, and TryGetProperty on Undefined *throws* rather than returning false.
			if (data.ValueKind == JsonValueKind.Object
				&& data.TryGetProperty("heartbeat_interval", out JsonElement interval)
				&& interval.TryGetInt32(out int milliseconds)
				&& milliseconds > 0)
			{
				return TimeSpan.FromMilliseconds(milliseconds);
			}

			// Discord has sent 41250ms for years. A default only matters if HELLO ever arrives malformed, and beating
			// too often is survivable where not beating at all is not.
			return TimeSpan.FromSeconds(41.25);
		}

		static string? ReadString(JsonElement element, string name)
			=> element.ValueKind == JsonValueKind.Object
				&& element.TryGetProperty(name, out JsonElement value)
				&& value.ValueKind == JsonValueKind.String
					? value.GetString()
					: null;

		/// <summary>
		/// State belonging to one connection, as opposed to the session that survives across them.
		/// </summary>
		sealed class SessionState
		{
			public SemaphoreSlim SendGate { get; } = new SemaphoreSlim(1, 1);

			public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(41.25);

			/// <summary>Whether a heartbeat is outstanding. Written by both loops, hence volatile.</summary>
			public volatile bool AckPending;

			/// <summary>Whether the heartbeat loop declared this connection dead.</summary>
			public volatile bool Zombied;

			/// <summary>Whether this connection ever reached READY or RESUMED.</summary>
			public volatile bool Established;
		}
	}
}
