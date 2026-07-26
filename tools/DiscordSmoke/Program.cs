// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using HordeServer;
using HordeServer.Acls;
using HordeServer.Discord.Client;
using HordeServer.Discord.Notifications;
using HordeServer.Plugins;
using HordeServer.Streams;
using HordeTestDoubles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
		static async Task<int> Main(string[] args)
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

			using DiscordClient client = CreateClient(settings);
			DiscordNotificationProcessor processor = CreateProcessor(settings, client);

			int failures = 0;

			foreach (Scenario scenario in chosen)
			{
				Console.Write($"  {scenario.Name,-24} {scenario.Description} ... ");

				try
				{
					await scenario.RunAsync(processor, CancellationToken.None);
					Console.WriteLine("sent");
				}
				catch (Exception ex)
				{
					// Keep going. One scenario failing is a result, not a reason to stop looking at the others.
					failures++;
					Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
				}
			}

			Console.WriteLine();
			Console.WriteLine(failures == 0
				? "All scenarios were accepted by Discord. Go and look at the channel - delivery is not legibility."
				: $"{failures} scenario(s) threw. Anything logged above as a Discord API error is a permissions or "
					+ "id problem rather than a bug in the message.");

			return failures == 0 ? 0 : 1;
		}

		static void PrintUsage()
		{
			Console.WriteLine("""
				Posts one of every Horde notification to a Discord channel, so the formatting can be looked at.

				  dotnet run --project tools/DiscordSmoke -c Development                 all scenarios
				  dotnet run --project tools/DiscordSmoke -c Development -- step label   just those

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

		static DiscordClient CreateClient(SmokeSettings settings)
		{
			ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder
				.AddSimpleConsole(options => options.SingleLine = true)
				.SetMinimumLevel(LogLevel.Information));

			return DiscordClient.Create(
				Options.Create(ServerConfig(settings)),
				new DiscordRateLimiter(loggerFactory.CreateLogger<DiscordRateLimiter>()),
				loggerFactory.CreateLogger<DiscordClient>());
		}

		static DiscordNotificationProcessor CreateProcessor(SmokeSettings settings, DiscordClient client)
		{
			ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder
				.AddSimpleConsole(options => options.SingleLine = true)
				.SetMinimumLevel(LogLevel.Information));

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
