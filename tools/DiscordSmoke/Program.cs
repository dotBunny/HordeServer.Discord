// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using HordeServer;
using HordeServer.Acls;
using HordeServer.Discord.Client;
using HordeServer.Discord.Notifications;
using HordeServer.Plugins;
using HordeServer.Streams;
using HordeTestDoubles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PluginProbe;
using System.Runtime.CompilerServices;

namespace DiscordSmoke
{
	/// <summary>
	/// Posts one of every notification this plugin produces to a real Discord channel.
	/// </summary>
	/// <remarks>
	/// The gap the unit tests cannot close. They prove what the plugin *would* send, asserted on the JSON; this
	/// proves what Discord does with it - whether embeds render legibly, whether colours read as intended, whether
	/// the bot can actually see the channel, whether a truncated code block survives the trip. None of that is
	/// knowable without sending.
	///
	/// It needs no Horde server, no MongoDB and no Redis: the notifications are built from stand-ins and handed
	/// straight to <see cref="DiscordNotificationProcessor"/>, which is the same object the sink calls.
	/// </remarks>
	static class Program
	{
		/// <summary>
		/// Points the runtime at the built Horde server before anything here touches a Horde type.
		/// </summary>
		/// <remarks>
		/// This tool references the engine assemblies with <c>Private=false</c>, so they are not beside its own
		/// binaries and the default load context cannot find them. The plugin resolves them because inside a real
		/// server it *is* the server that owns them; here nothing does, so the resolver has to be installed by hand.
		///
		/// Nothing but the install may happen in this method. The JIT resolves every type a method references when
		/// it compiles that method, so mentioning a Horde type here - even in a variable that is never used - would
		/// fail before the handler was attached. <see cref="RunAsync"/> is where the tool actually starts, and it is
		/// <see cref="MethodImplOptions.NoInlining"/> so it cannot be folded back into this one.
		/// </remarks>
		static async Task<int> Main(string[] args)
		{
			string? appDir = HordeBinDirLocator.Resolve(typeof(Program).Assembly);

			if (appDir == null)
			{
				Console.Error.WriteLine(HordeBinDirLocator.NotFoundMessage);
				return 2;
			}

			if (!Directory.Exists(appDir))
			{
				Console.Error.WriteLine($"Horde server directory does not exist: {appDir}");
				return 2;
			}

			EngineAssemblyResolver.Install(appDir);

			return await RunAsync(args);
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		static async Task<int> RunAsync(string[] args)
		{
			if (args.Contains("--help") || args.Contains("-h"))
			{
				PrintUsage();
				return 0;
			}

			if (!SmokeSettings.TryResolve(out SmokeSettings? settings, out string? problem))
			{
				Console.Error.WriteLine(problem);
				return 1;
			}

			Console.WriteLine("Discord smoke test");
			Console.WriteLine();
			Console.WriteLine(settings!.Describe());
			Console.WriteLine();

			if (args.Contains("--gateway"))
			{
				return await RunGatewayAsync(settings, HoldSeconds(args));
			}

			if (args.Contains("--interact"))
			{
				return await RunInteractAsync(settings, HoldSeconds(args, 120));
			}

			if (args.Contains("--modal"))
			{
				return await RunModalAsync(settings, HoldSeconds(args, 180));
			}

			IReadOnlyList<Scenario> all = Scenarios.All(settings);
			IReadOnlyList<Scenario> chosen = Choose(all, args, out string? unknown);

			if (unknown != null)
			{
				Console.Error.WriteLine($"Unknown scenario '{unknown}'.");
				Console.Error.WriteLine();
				PrintUsage();
				return 1;
			}

			if (!settings.CanReachAUser)
			{
				Console.WriteLine("No DiscordTestUserId is set, so nothing will be sent as a direct message - the");
				Console.WriteLine("scenarios that would be will fall back to the channel, which is also worth seeing.");
				Console.WriteLine();
			}

			Console.WriteLine($"Sending {chosen.Count} scenario(s) to channel {settings.ChannelId}:");

			using SmokeLog log = new SmokeLog();
			using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder
				.AddProvider(log)
				.SetMinimumLevel(LogLevel.Warning));

			using DiscordClient client = CreateClient(settings, loggerFactory);
			DiscordNotificationProcessor processor = CreateProcessor(settings, client, loggerFactory);

			int failures = 0;

			foreach (Scenario scenario in chosen)
			{
				Console.Write($"  {scenario.Name,-24} {scenario.Description} ... ");
				log.Clear();

				try
				{
					await scenario.RunAsync(processor, CancellationToken.None);
				}
				catch (Exception ex)
				{
					// Keep going. One scenario failing is a result, not a reason to stop looking at the others.
					failures++;
					Console.WriteLine($"THREW: {ex.GetType().Name}: {ex.Message}");
					continue;
				}

				// Returning normally is not the same as arriving. The client logs a rejected request rather than
				// throwing, so a scenario is only sent if it also had nothing to complain about.
				IReadOnlyList<string> problems = log.Problems;

				if (problems.Count == 0)
				{
					Console.WriteLine("sent");
					continue;
				}

				failures++;
				Console.WriteLine($"REJECTED ({problems.Count})");

				foreach (string message in problems)
				{
					Console.WriteLine($"    {message}");
				}
			}

			Console.WriteLine();

			if (failures == 0)
			{
				Console.WriteLine("All scenarios were accepted by Discord. Go and look at the channel - delivery is "
					+ "not legibility.");
				return 0;
			}

			Console.WriteLine($"{failures} of {chosen.Count} scenario(s) did not arrive.");
			Console.WriteLine();
			Console.WriteLine(Diagnose(log.SeenCodes));

			return 1;
		}

		/// <summary>
		/// Opens a real gateway connection and reports what it does.
		/// </summary>
		/// <remarks>
		/// The counterpart to the scenarios, for the inbound half. The unit tests drive the state machine through a
		/// scripted socket and prove it reconnects correctly; they cannot prove the bot token is accepted at
		/// identify, that intents of zero are allowed, or that the heartbeat interval Discord actually sends is the
		/// one the code expects. Holding the connection past one interval is what checks the last of those, which is
		/// why the hold is adjustable and worth setting above 41 seconds at least once.
		/// </remarks>
		static async Task<int> RunGatewayAsync(SmokeSettings settings, int holdSeconds)
		{
			using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder
				.AddSimpleConsole(options => options.SingleLine = true)
				.SetMinimumLevel(LogLevel.Information));

			using DiscordClient client = CreateClient(settings, loggerFactory);
			using DiscordGateway gateway = new DiscordGateway(
				Options.Create(ServerConfig(settings)),
				client,
				loggerFactory.CreateLogger<DiscordGateway>());

			int dispatches = 0;
			gateway.DispatchReceived += dispatch =>
			{
				dispatches++;
				Console.WriteLine($"  dispatch: {dispatch.EventName}");
			};

			Console.WriteLine($"Connecting to the gateway, holding for {holdSeconds}s.");

			using CancellationTokenSource stopping = new CancellationTokenSource();
			Task running = gateway.RunAsync(stopping.Token);

			DateTime deadline = DateTime.UtcNow.AddSeconds(holdSeconds);

			while (DateTime.UtcNow < deadline && !running.IsCompleted)
			{
				await Task.Delay(TimeSpan.FromMilliseconds(250.0));
			}

			bool connected = gateway.IsConnected;

			await stopping.CancelAsync();
			await running;

			Console.WriteLine();

			if (!connected)
			{
				Console.WriteLine("The gateway never reached READY. The log above says why; 4004 is a bad bot token "
					+ "and 4014 is a privileged intent that was requested but not granted.");
				return 1;
			}

			Console.WriteLine($"Connected as {gateway.BotUsername} ({gateway.BotUserId}), {dispatches} dispatch(es) "
				+ "received.");
			Console.WriteLine(holdSeconds > 45
				? "The hold covered a full heartbeat interval, so the heartbeat was acknowledged at least once."
				: "Held for less than one heartbeat interval - re-run with --gateway 50 to exercise the heartbeat.");

			return 0;
		}

		/// <summary>
		/// Posts a message with buttons and waits for somebody to press one.
		/// </summary>
		/// <remarks>
		/// The only check that covers the whole inbound path at once: a component serialised the way Discord accepts
		/// it, an <c>INTERACTION_CREATE</c> arriving over the socket, the acknowledgement landing inside the
		/// three-second deadline, and the message being edited through the interaction token afterwards. The unit
		/// tests assert each of those against a fake; none of them can tell you Discord agrees.
		///
		/// Needs a human, which is the point. If the acknowledgement is too slow or malformed, what you see is the
		/// client showing "This interaction failed" - a symptom that exists nowhere in any log.
		/// </remarks>
		static async Task<int> RunInteractAsync(SmokeSettings settings, int holdSeconds)
		{
			using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder
				.AddSimpleConsole(options => options.SingleLine = true)
				.SetMinimumLevel(LogLevel.Information));

			IOptions<DiscordServerConfig> serverConfig = Options.Create(ServerConfig(settings));

			using DiscordClient client = CreateClient(settings, loggerFactory);
			using DiscordGateway gateway = new DiscordGateway(serverConfig, client, loggerFactory.CreateLogger<DiscordGateway>());

			DiscordInteractionRouter router = new DiscordInteractionRouter(
				gateway, client, serverConfig, loggerFactory.CreateLogger<DiscordInteractionRouter>());

			int presses = 0;

			router.Register(DiscordCustomId.IssueScope, async (context, cancellationToken) =>
			{
				presses++;
				Console.WriteLine($"  pressed: {context.CustomId.Verb} by user {context.DiscordUserId}");

				DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
					.WithTitle("Interaction round trip")
					.WithColor(0x57F287)
					.AddField("Verb", context.CustomId.Verb, true)
					.AddField("Pressed by", $"<@{context.DiscordUserId}>", true);

				// Through the interaction token, and with the buttons removed - the same two things a resolved
				// triage message does.
				bool edited = await router.UpdateMessageAsync(
					context,
					new DiscordMessageBuilder().AddEmbed(embed).WithoutComponents().Build(),
					cancellationToken);

				Console.WriteLine(edited ? "  message updated" : "  message could NOT be updated");
			});

			DiscordMessage message = new DiscordMessageBuilder()
				.AddEmbed(new DiscordEmbedBuilder()
					.WithTitle("Interaction round trip")
					.WithDescription("Press a button. This message should rewrite itself and the buttons should go away.")
					.WithColor(0xFEE75C))
				.WithComponents(new DiscordComponentBuilder()
					.AddButton(new DiscordCustomId(DiscordCustomId.IssueScope, "smoke", "ack").ToString(), "Acknowledge", DiscordButtonStyle.Success)
					.AddButton(new DiscordCustomId(DiscordCustomId.IssueScope, "smoke", "decline").ToString(), "Decline", DiscordButtonStyle.Danger)
					.AddLink(settings.DashboardUrl.ToString(), "Open in Horde"))
				.Build();

			if (await client.CreateMessageAsync(settings.ChannelId, message, CancellationToken.None) == null)
			{
				Console.Error.WriteLine("The message with the buttons could not be posted; nothing to press.");
				return 1;
			}

			Console.WriteLine($"Posted a message with buttons to channel {settings.ChannelId}.");
			Console.WriteLine($"Go and press one - waiting {holdSeconds}s.");
			Console.WriteLine();

			using CancellationTokenSource stopping = new CancellationTokenSource();
			Task running = gateway.RunAsync(stopping.Token);

			await router.StartAsync(CancellationToken.None);

			DateTime deadline = DateTime.UtcNow.AddSeconds(holdSeconds);

			while (DateTime.UtcNow < deadline && !running.IsCompleted)
			{
				await Task.Delay(TimeSpan.FromMilliseconds(250.0));
			}

			await router.StopAsync(CancellationToken.None);
			await stopping.CancelAsync();
			await running;

			Console.WriteLine();
			Console.WriteLine(presses > 0
				? $"{presses} interaction(s) round-tripped. Check the channel: the message should have rewritten "
					+ "itself and lost its buttons."
				: "Nobody pressed anything, so the inbound path is still unproven. Re-run and press a button.");

			return presses > 0 ? 0 : 1;
		}

		/// <summary>
		/// Drives the whole hybrid Mark Fixed flow against a real Discord client.
		/// </summary>
		/// <remarks>
		/// Button → modal → conditional ephemeral dropdown, which is the design in <c>.claude/PLAN.md</c> section
		/// 3.3.4 and the part of Phase 4 with the most ways to be subtly wrong. Two of them only show up here: a
		/// modal that is opened after a deferral is refused by Discord, and an ephemeral followup posted against a
		/// stale token vanishes without an error anyone sees.
		///
		/// Type something into "Root cause summary" to get the category dropdown; leave it blank to take the common
		/// path, which is the one this flow is shaped around.
		/// </remarks>
		static async Task<int> RunModalAsync(SmokeSettings settings, int holdSeconds)
		{
			using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder
				.AddSimpleConsole(options => options.SingleLine = true)
				.SetMinimumLevel(LogLevel.Information));

			IOptions<DiscordServerConfig> serverConfig = Options.Create(ServerConfig(settings));

			using DiscordClient client = CreateClient(settings, loggerFactory);
			using DiscordGateway gateway = new DiscordGateway(serverConfig, client, loggerFactory.CreateLogger<DiscordGateway>());

			DiscordInteractionRouter router = new DiscordInteractionRouter(
				gateway, client, serverConfig, loggerFactory.CreateLogger<DiscordInteractionRouter>());

			int completed = 0;

			router.Register(
				DiscordCustomId.IssueScope,
				async (context, cancellationToken) =>
				{
					switch (context.CustomId.Verb)
					{
						case "markfixed":
							// Unacknowledged, because a modal can only ever be the first answer.
							Console.WriteLine("  button pressed: opening the modal");

							await router.RespondAsync(context, DiscordInteractionResponse.OpenModal(
								new DiscordModalBuilder(new DiscordCustomId(DiscordCustomId.IssueScope, context.CustomId.Id, "fixsubmit").ToString(), "Mark Fixed")
									.AddTextInput("fix_cl", "Fix CL", required: true, placeholder: "12345")
									.AddTextInput("rootcause_summary", "Root cause summary", paragraph: true, placeholder: "Fill this in to get the category dropdown")
									.AddTextInput("rootcause_cl", "Root cause CL")
									.AddTextInput("rootcause_dupeid", "Duplicate issue id")
									.Build()),
								cancellationToken);
							break;

						case "fixsubmit":
							// Already acknowledged by the router, so applying the fix could take as long as it likes.
							IReadOnlyDictionary<string, string> values = context.Interaction.GetModalValues();
							string summary = values.GetValueOrDefault("rootcause_summary", String.Empty);

							Console.WriteLine($"  modal submitted: fix_cl='{values.GetValueOrDefault("fix_cl")}', "
								+ $"summary={(String.IsNullOrWhiteSpace(summary) ? "<blank>" : "given")}");

							await router.UpdateMessageAsync(
								context,
								new DiscordMessageBuilder()
									.AddEmbed(new DiscordEmbedBuilder()
										.WithTitle("Marked fixed")
										.WithColor(0x57F287)
										.AddField("Fix CL", values.GetValueOrDefault("fix_cl", "<none>"), true)
										.AddField("Marked by", $"<@{context.DiscordUserId}>", true))
									.WithoutComponents()
									.Build(),
								cancellationToken);

							if (String.IsNullOrWhiteSpace(summary))
							{
								// The common path: closing out a fix stayed a single interaction.
								completed++;
								Console.WriteLine("  no root cause summary, so no category asked for - flow complete");
								break;
							}

							Console.WriteLine("  root cause summary given, asking for a category");

							await router.FollowUpAsync(
								context,
								new DiscordMessageBuilder()
									.WithContent("One more thing - what kind of root cause was it?")
									.WithComponents(new DiscordComponentBuilder().AddSelect(
										new DiscordCustomId(DiscordCustomId.IssueScope, context.CustomId.Id, "category").ToString(),
										RootCauseCategories(),
										"Pick a category"))
									.Build(),
								ephemeral: true,
								cancellationToken);
							break;

						case "category":
							string chosen = context.Interaction.Data?.Values?.FirstOrDefault() ?? "<none>";
							completed++;

							Console.WriteLine($"  category chosen: {chosen} - flow complete");

							await router.UpdateMessageAsync(
								context,
								new DiscordMessageBuilder().WithContent($"Recorded root cause: **{chosen}**").WithoutComponents().Build(),
								cancellationToken);
							break;

						default:
							Console.WriteLine($"  unexpected verb '{context.CustomId.Verb}'");
							break;
					}
				},
				// Only the modal-opening verb gives up its deferral.
				customId => customId.Verb == "markfixed");

			DiscordMessage message = new DiscordMessageBuilder()
				.AddEmbed(new DiscordEmbedBuilder()
					.WithTitle("🔴 Compile Win64")
					.WithDescription("Press **Mark Fixed** to open the modal. Fill in the root cause summary to be "
						+ "asked for a category afterwards, or leave it blank for the common path.")
					.WithColor(0xED4245))
				.WithComponents(new DiscordComponentBuilder().AddButton(
					new DiscordCustomId(DiscordCustomId.IssueScope, "smoke", "markfixed").ToString(),
					"Mark Fixed",
					DiscordButtonStyle.Primary))
				.Build();

			if (await client.CreateMessageAsync(settings.ChannelId, message, CancellationToken.None) == null)
			{
				Console.Error.WriteLine("The message could not be posted; nothing to press.");
				return 1;
			}

			Console.WriteLine($"Posted a Mark Fixed button to channel {settings.ChannelId}.");
			Console.WriteLine($"Go and use it - waiting {holdSeconds}s.");
			Console.WriteLine();

			using CancellationTokenSource stopping = new CancellationTokenSource();
			Task running = gateway.RunAsync(stopping.Token);

			await router.StartAsync(CancellationToken.None);

			DateTime deadline = DateTime.UtcNow.AddSeconds(holdSeconds);

			while (DateTime.UtcNow < deadline && !running.IsCompleted && completed == 0)
			{
				await Task.Delay(TimeSpan.FromMilliseconds(250.0));
			}

			await router.StopAsync(CancellationToken.None);
			await stopping.CancelAsync();
			await running;

			Console.WriteLine();
			Console.WriteLine(completed > 0
				? "The hybrid flow round-tripped end to end."
				: "The flow was not completed, so it is still unproven. Re-run and press Mark Fixed.");

			return completed > 0 ? 0 : 1;
		}

		/// <summary>
		/// Stand-ins for Horde's root cause vocabulary, which is twelve options in the Slack view.
		/// </summary>
		static List<DiscordSelectOption> RootCauseCategories()
			=> [.. new[] { "Code", "Content", "Configuration", "Infrastructure", "Flaky test", "Unknown" }
				.Select(x => new DiscordSelectOption { Label = x, Value = x.ToLowerInvariant().Replace(' ', '-') })];

		/// <summary>
		/// How long a holding mode should wait, from the first number on the command line.
		/// </summary>
		static int HoldSeconds(string[] args, int fallback = 15)
		{
			foreach (string arg in args)
			{
				if (Int32.TryParse(arg, out int seconds) && seconds > 0)
				{
					return seconds;
				}
			}

			return fallback;
		}

		static void PrintUsage()
		{
			Console.WriteLine("""
				Posts one of every Horde notification to a Discord channel, so the formatting can be looked at.

				  dotnet run --project tools/DiscordSmoke -c Development                    all scenarios
				  dotnet run --project tools/DiscordSmoke -c Development -- step label      just those
				  dotnet run --project tools/DiscordSmoke -c Development -- --gateway 50    connect the gateway
				                                                                            instead, holding for 50s
				  dotnet run --project tools/DiscordSmoke -c Development -- --interact     post buttons and wait
				                                                                            for someone to press one

				Credentials come from Horde.local.props (git-ignored) or the DISCORD_* environment variables.
				See Horde.local.props.template.

				Scenarios:
				""");

			foreach (Scenario scenario in Scenarios.All(Placeholder()))
			{
				Console.WriteLine($"  {scenario.Name,-24} {scenario.Description}");
			}
		}

		static IReadOnlyList<Scenario> Choose(IReadOnlyList<Scenario> all, string[] args, out string? unknown)
		{
			IReadOnlyList<string> names = [.. args.Where(x => !x.StartsWith('-'))];

			if (names.Count == 0)
			{
				unknown = null;
				return all;
			}

			List<Scenario> chosen = new List<Scenario>();

			foreach (string name in names)
			{
				Scenario? scenario = all.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

				if (scenario == null)
				{
					unknown = name;
					return [];
				}

				chosen.Add(scenario);
			}

			unknown = null;
			return chosen;
		}

		/// <summary>
		/// Explains the Discord error codes a run produced, in the order they are worth acting on.
		/// </summary>
		/// <remarks>
		/// Every one of these is a setup problem on the Discord side rather than a bug here, and every one of them
		/// costs a while to work out from the raw code - which is the argument for writing them down once.
		/// </remarks>
		static string Diagnose(IReadOnlyCollection<int> codes)
		{
			if (codes.Count == 0)
			{
				return "Nothing was rejected with a Discord error code, so look at the messages logged above.";
			}

			List<string> notes = new List<string>();

			foreach (int code in codes)
			{
				notes.Add(code switch
				{
					10003 => "  10003 Unknown Channel - DiscordTestChannelId does not name a channel the bot can see. "
						+ "Check the id, and that it is in DiscordGuildId.",
					40001 => "  40001 Unauthorized - DiscordBotToken is wrong or was regenerated. Copy it again from "
						+ "the application's Bot page.",
					50001 => "  50001 Missing Access - the bot cannot see the channel. Invite it to the guild, then "
						+ "grant it View Channel, Send Messages and Embed Links *on that channel* - a role permission "
						+ "is overridden by a channel one.",
					50007 => "  50007 Cannot Send Messages To This User - the recipient does not accept direct "
						+ "messages from server members. Enable them for the test guild, under Privacy Settings.",
					50013 => "  50013 Missing Permissions - the bot can see the channel but may not post in it. Embed "
						+ "Links is the one usually missing, and without it every notification is dropped.",
					50035 => "  50035 Invalid Form Body - Discord rejected the message itself. Unlike the others this "
						+ "IS a bug here; the body logged above says which field.",
					50278 => "  50278 No Mutual Guilds - DiscordTestUserId is not a member of DiscordGuildId, or is "
						+ "not the id it looks like. A bot may only DM someone who shares a server with it.",
					_ => $"  {code} - look it up in Discord's error code list; the logged body says more.",
				});
			}

			return String.Join(Environment.NewLine, notes);
		}

		static DiscordClient CreateClient(SmokeSettings settings, ILoggerFactory loggerFactory)
		{
			return DiscordClient.Create(
				Options.Create(ServerConfig(settings)),
				new DiscordRateLimiter(loggerFactory.CreateLogger<DiscordRateLimiter>()),
				loggerFactory.CreateLogger<DiscordClient>());
		}

		static DiscordNotificationProcessor CreateProcessor(SmokeSettings settings, DiscordClient client, ILoggerFactory loggerFactory)
		{
			IOptions<DiscordServerConfig> serverConfig = Options.Create(ServerConfig(settings));
			IOptions<BuildServerConfig> buildServerConfig = Options.Create(new BuildServerConfig());
			StaticOptionsMonitor<DiscordConfig> pluginConfig = new StaticOptionsMonitor<DiscordConfig>(PluginConfig(settings, loggerFactory));

			FakeUserCollection users = new FakeUserCollection();
			users.Add(Scenarios.Recipient());

			return new DiscordNotificationProcessor(
				client,
				new DiscordChannelResolver(pluginConfig, serverConfig, buildServerConfig, loggerFactory.CreateLogger<DiscordChannelResolver>()),
				new DiscordUserResolver(pluginConfig, loggerFactory.CreateLogger<DiscordUserResolver>()),
				new DiscordRepeatFilter(),
				serverConfig,
				buildServerConfig,
				new StaticOptionsMonitor<BuildConfig>(new BuildConfig()),
				users,
				// There is no Horde behind this tool, so a thread url written back goes nowhere. Harmless: the
				// scenarios post to a mapped channel and the thread is created for real either way.
				new FakeHordeIssues(),
				new FakeServerInfo { DashboardUrl = settings.DashboardUrl },
				loggerFactory.CreateLogger<DiscordNotificationProcessor>());
		}

		/// <summary>
		/// Server configuration pointing every base category at the one test channel.
		/// </summary>
		/// <remarks>
		/// Uses the Discord-native overrides rather than the translation map, because the base categories are the
		/// half of routing that has no Slack id to translate from when there is no Horde server behind this.
		/// </remarks>
		static DiscordServerConfig ServerConfig(SmokeSettings settings) => new DiscordServerConfig
		{
			BotToken = settings.BotToken,
			GuildId = settings.GuildId,
			JobNotificationChannel = settings.ChannelId,
			AgentNotificationChannel = settings.ChannelId,
			ConfigNotificationChannel = settings.ChannelId,
			UpdateStreamsNotificationChannel = settings.ChannelId,
			DeviceNotificationChannel = settings.ChannelId,
		};

		/// <summary>
		/// Plugin configuration mapping the smoke channel id and, if one was given, the smoke user.
		/// </summary>
		static DiscordConfig PluginConfig(SmokeSettings settings, ILoggerFactory loggerFactory)
		{
			DiscordConfig config = new DiscordConfig
			{
				Guilds = { ["smoke"] = settings.GuildId },
				Channels =
				{
					[Scenarios.SmokeChannelId] = new DiscordChannelMapping
					{
						Label = "smoke-test",
						Guild = "smoke",
						Channel = settings.ChannelId,
					},
				},
			};

			if (settings.UserId != null)
			{
				config.UserMap[Scenarios.RecipientEmail] = settings.UserId;
			}

			if (settings.RoleId != null)
			{
				config.Roles[Scenarios.TriageAlias] = new DiscordRoleMapping
				{
					Label = "smoke-triage",
					Guild = "smoke",
					Role = settings.RoleId,
				};
			}

			config.PostLoad(new PluginConfigOptions(
				ConfigVersion.Latest,
				Array.Empty<IPluginConfig>(),
				new AclConfig(),
				loggerFactory.CreateLogger<DiscordConfig>()));

			return config;
		}

		/// <summary>
		/// Settings good enough to enumerate the scenario list, for the usage text.
		/// </summary>
		static SmokeSettings Placeholder() => new SmokeSettings
		{
			BotToken = String.Empty,
			GuildId = "0",
			ChannelId = "0",
			DashboardUrl = new Uri("https://horde.example.com/"),
		};
	}
}
