// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using EpicGames.Horde.Jobs;
using HordeServer.Acls;
using HordeServer.Discord.Notifications;
using HordeServer.Plugins;
using HordeTestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HordeServer.Discord.Tests.Notifications
{
	/// <summary>
	/// Tests for translating Horde's Slack channel ids into Discord destinations.
	/// </summary>
	[TestClass]
	public sealed class DiscordChannelResolverTests
	{
		const string TriageSlackId = "C0832ESJUR5";
		const string BuildsSlackId = "C085J3A6FHN";
		const string UnknownSlackId = "C0ZZZZZZZZZ";

		const string TriageChannel = "998877665544332211";
		const string BuildsChannel = "112233445566778899";
		const string FallbackChannel = "555566667777888899";
		const string GuildId = "100000000000000001";

		[TestMethod]
		public void MappedChannelResolvesToItsDiscordDestination()
		{
			DiscordChannelResolver resolver = Create(Mapped());

			DiscordDestination? destination = resolver.Resolve(TriageSlackId);

			Assert.IsNotNull(destination);
			Assert.AreEqual(TriageChannel, destination.ChannelId);
			Assert.AreEqual("horde-triage", destination.Label);
			Assert.IsFalse(destination.IsFallback);
		}

		[TestMethod]
		public void LookupIsCaseInsensitive()
		{
			DiscordChannelResolver resolver = Create(Mapped());

			Assert.AreEqual(TriageChannel, resolver.Resolve(TriageSlackId.ToLowerInvariant())?.ChannelId);
		}

		[TestMethod]
		public void SingleConfiguredGuildBecomesTheDefault()
		{
			DiscordChannelResolver resolver = Create(Mapped());

			Assert.AreEqual(GuildId, resolver.Resolve(BuildsSlackId)?.GuildId,
				"The builds mapping names no guild, and naming one is pointless when there is only a single guild "
				+ "to choose from.");
		}

		[TestMethod]
		public void UnmappedChannelGoesToTheFallbackCarryingItsOrigin()
		{
			DiscordChannelResolver resolver = Create(Mapped());

			DiscordDestination? destination = resolver.Resolve(UnknownSlackId);

			Assert.IsNotNull(destination);
			Assert.AreEqual(FallbackChannel, destination.ChannelId);
			Assert.IsTrue(destination.IsFallback);
			Assert.AreEqual(UnknownSlackId, destination.SourceChannel,
				"The fallback message has to say what it was meant for, or the catch-all fills with untraceable "
				+ "notifications.");
		}

		[TestMethod]
		public void UnmappedChannelIsDroppedWhenThereIsNoFallback()
		{
			DiscordConfig config = Mapped();
			config.FallbackChannel = null;
			PostLoad(config);

			Assert.IsNull(Create(config).Resolve(UnknownSlackId));
		}

		[TestMethod]
		public void NothingResolvesFromAnEmptySetting()
		{
			DiscordChannelResolver resolver = Create(Mapped());

			Assert.IsNull(resolver.Resolve(null));
			Assert.IsNull(resolver.Resolve("   "), "An unset Horde channel is not an unmapped one; it must not "
				+ "reach the fallback.");
		}

		[TestMethod]
		public void DestinationsAreDeduplicated()
		{
			DiscordConfig config = Mapped();
			config.Channels[UnknownSlackId] = new DiscordChannelMapping { Label = "also-triage", Channel = TriageChannel };
			PostLoad(config);

			IReadOnlyList<DiscordDestination> destinations = Create(config).ResolveAll(new[] { TriageSlackId, UnknownSlackId });

			Assert.AreEqual(1, destinations.Count,
				"Two Horde channels pointing at one Discord channel must not produce two identical messages.");
		}

		[TestMethod]
		public void BaseCategoryFollowsTheBuildPluginsSlackSetting()
		{
			DiscordChannelResolver resolver = Create(
				Mapped(),
				buildServerConfig: new BuildServerConfig { JobNotificationChannel = BuildsSlackId });

			IReadOnlyList<DiscordDestination> destinations = resolver.ResolveCategory(DiscordChannelCategory.Job);

			Assert.AreEqual(1, destinations.Count);
			Assert.AreEqual(BuildsChannel, destinations[0].ChannelId,
				"Routing should be configured once in Horde, not restated on the Discord side.");
		}

		[TestMethod]
		public void DiscordSideOverrideWinsOverTheBuildPluginSetting()
		{
			DiscordChannelResolver resolver = Create(
				Mapped(),
				serverConfig: new DiscordServerConfig { JobNotificationChannel = TriageChannel },
				buildServerConfig: new BuildServerConfig { JobNotificationChannel = BuildsSlackId });

			IReadOnlyList<DiscordDestination> destinations = resolver.ResolveCategory(DiscordChannelCategory.Job);

			Assert.AreEqual(1, destinations.Count);
			Assert.AreEqual(TriageChannel, destinations[0].ChannelId,
				"A deployment running Discord without Slack has to be able to route without inventing Slack ids.");
		}

		[TestMethod]
		public void ASlackIdInTheDiscordOverrideIsRejectedRatherThanUsed()
		{
			DiscordChannelResolver resolver = Create(
				Mapped(),
				serverConfig: new DiscordServerConfig { JobNotificationChannel = BuildsSlackId });

			Assert.AreEqual(0, resolver.ResolveCategory(DiscordChannelCategory.Job).Count,
				"Treating a Slack id as a Discord snowflake would post nowhere; better to reject it and say why.");
		}

		[TestMethod]
		public void MultipleChannelsInOneSettingAllResolve()
		{
			DiscordChannelResolver resolver = Create(
				Mapped(),
				buildServerConfig: new BuildServerConfig { JobNotificationChannel = $"{TriageSlackId};{BuildsSlackId}" });

			Assert.AreEqual(2, resolver.ResolveCategory(DiscordChannelCategory.Job).Count);
		}

		[TestMethod]
		public void ABareChannelNameIsAValidKey()
		{
			// jobNotificationChannel and updateStreamsNotificationChannel hold a name, not an id - the Slack sink
			// prepends the '#' itself - so the map has to accept one without complaining.
			DiscordConfig config = new DiscordConfig
			{
				Channels = { ["horde-builds"] = new DiscordChannelMapping { Channel = BuildsChannel } },
			};

			PostLoad(config);

			DiscordChannelResolver resolver = Create(
				config,
				buildServerConfig: new BuildServerConfig { JobNotificationChannel = "horde-builds" });

			Assert.AreEqual(BuildsChannel, resolver.ResolveCategory(DiscordChannelCategory.Job)[0].ChannelId);
		}

		// The outcome is passed as a string and parsed in the body, not as a LabelOutcome. A DataRow carrying an
		// engine type makes MSTest drop the whole method during discovery - it reads the attribute before this
		// assembly's module initializer has installed the engine assembly resolver, so EpicGames.Horde cannot load.
		// The test vanishes from the run and the summary still reads green, which is the worst possible failure mode.
		[TestMethod]
		[DataRow("", "Failure", true)]
		[DataRow("Failure", "Failure", true)]
		[DataRow("Failure", "Success", false)]
		[DataRow("Failure|Warnings", "Warnings", true)]
		[DataRow("Failure|Warnings", "Success", false)]
		[DataRow("failure", "Failure", true)]
		[DataRow("Nonsense", "Failure", false)]
		public void OutcomeFilterFollowsHordesSemantics(string filter, string outcome, bool expected)
			=> Assert.AreEqual(expected, Create(Mapped()).PassesFilter(filter, Enum.Parse<LabelOutcome>(outcome), "C0832ESJUR5"));

		[TestMethod]
		public void AnUnsetOutcomeFilterPassesEverything()
		{
			DiscordChannelResolver resolver = Create(Mapped());

			Assert.IsTrue(resolver.PassesFilter(null, LabelOutcome.Failure, "C0832ESJUR5"));
			Assert.IsTrue(resolver.PassesFilter("   ", LabelOutcome.Success, "C0832ESJUR5"));
		}

		[TestMethod]
		public void MappingToSomethingThatIsNotASnowflakeIsDiscarded()
		{
			DiscordConfig config = new DiscordConfig
			{
				Channels = { [TriageSlackId] = new DiscordChannelMapping { Channel = "#horde-triage" } },
			};

			PostLoad(config);

			Assert.AreEqual(0, config.ResolvedChannels.Count,
				"A bad mapping is dropped rather than accepted, so the routing report can flag the gap.");
		}

		[TestMethod]
		public void BadConfigurationDoesNotThrow()
		{
			DiscordConfig config = new DiscordConfig
			{
				Guilds = { ["studio"] = "not-a-guild" },
				DefaultGuild = "missing",
				FallbackChannel = "nope",
				Channels =
				{
					["not-a-slack-id"] = new DiscordChannelMapping { Channel = BuildsChannel },
					[TriageSlackId] = new DiscordChannelMapping { Guild = "absent", Channel = TriageChannel },
				},
			};

			PostLoad(config);

			// PostLoad runs inside the server's config reload. Throwing would fail the whole reload and take the
			// other plugins' configuration down with it, over a Discord channel being wrong.
			Assert.IsNull(config.ResolvedFallback);
			Assert.IsNull(config.ResolvedDefaultGuildId);
			Assert.AreEqual(2, config.ResolvedChannels.Count);
			Assert.IsNull(config.ResolvedChannels[TriageSlackId].GuildId, "An unresolvable guild leaves it unset.");
		}

		static DiscordConfig Mapped()
		{
			DiscordConfig config = new DiscordConfig
			{
				Guilds = { ["studio"] = GuildId },
				FallbackChannel = FallbackChannel,
				Channels =
				{
					[TriageSlackId] = new DiscordChannelMapping { Label = "horde-triage", Guild = "studio", Channel = TriageChannel },
					[BuildsSlackId] = new DiscordChannelMapping { Label = "horde-builds", Channel = BuildsChannel },
				},
			};

			PostLoad(config);
			return config;
		}

		static void PostLoad(DiscordConfig config)
			=> config.PostLoad(new PluginConfigOptions(ConfigVersion.Latest, Array.Empty<IPluginConfig>(), new AclConfig(), NullLogger.Instance));

		static DiscordChannelResolver Create(DiscordConfig config, DiscordServerConfig? serverConfig = null, BuildServerConfig? buildServerConfig = null)
			=> new DiscordChannelResolver(
				new StaticOptionsMonitor<DiscordConfig>(config),
				Options.Create(serverConfig ?? new DiscordServerConfig()),
				Options.Create(buildServerConfig ?? new BuildServerConfig()),
				NullLogger<DiscordChannelResolver>.Instance);
	}
}
