// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;
using HordeServer.Discord.Notifications;
using HordeServer.Plugins;
using Microsoft.Extensions.Logging;

namespace HordeServer
{
	/// <summary>
	/// Global (hot-reloadable) configuration for the Discord plugin.
	/// </summary>
	/// <remarks>
	/// This is the half of the configuration that changes as people, streams and teams come and go, so it
	/// deliberately lives here rather than in <see cref="DiscordServerConfig"/> - the config system reloads it
	/// without a server restart.
	///
	/// The channel map is the centre of it. Horde already decides *which* channel every notification belongs in,
	/// per workflow, per stream and per template, and hands the sink a Slack channel id. Rather than reproducing
	/// that routing, the plugin translates the last hop: Slack channel id in, Discord guild and channel out. That
	/// needs no changes to Epic-owned stream configuration, and every workflow that already has its own Slack
	/// channel automatically gets its own Discord one.
	/// </remarks>
	public class DiscordConfig : IPluginConfig
	{
		/// <summary>
		/// Map of Horde user email address to Discord user id (snowflake).
		/// </summary>
		/// <remarks>
		/// Discord has no equivalent of Slack's lookup-user-by-email, and Horde only knows a user's email address,
		/// so the association has to be supplied. Hand-maintained for now; a <c>/link</c> slash command can be added
		/// later as a second source without changing anything that consumes this.
		/// </remarks>
		public Dictionary<string, string> UserMap { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Named Discord guilds, mapping a short name to a guild snowflake.
		/// </summary>
		/// <remarks>
		/// Named rather than used inline so a guild id appears once. One bot token can serve any number of guilds it
		/// has been invited to; more than one *token* would be a larger change, because the global rate limit is per
		/// token.
		/// </remarks>
		public Dictionary<string, string> Guilds { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Guild used by channel mappings that do not name one.
		/// </summary>
		/// <remarks>Unnecessary when exactly one guild is configured - that one is the default.</remarks>
		public string? DefaultGuild { get; set; }

		/// <summary>
		/// Where each of Horde's channels lands, keyed by the Slack channel id Horde uses to address it.
		/// </summary>
		/// <remarks>
		/// Slack channel ids look like <c>C0832ESJUR5</c> and are what appears in workflow <c>reportChannel</c> and
		/// <c>triageChannel</c> settings, the issue and device reports, and the per-stream and per-template job
		/// notification channels. They are stable across channel renames, which is why they make a better key than a
		/// name would.
		///
		/// Two of the Build plugin's server settings are the exception and hold a **bare channel name** instead -
		/// <c>jobNotificationChannel</c> and <c>updateStreamsNotificationChannel</c>, where the Slack sink prepends
		/// the <c>#</c> itself. Key those entries on the name, without the <c>#</c>.
		/// </remarks>
		public Dictionary<string, DiscordChannelMapping> Channels { get; set; } = new Dictionary<string, DiscordChannelMapping>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Discord channel snowflake that anything unmapped is posted to.
		/// </summary>
		/// <remarks>
		/// A catch-all means a notification is never silently lost while the map is incomplete; messages that land
		/// here say which Horde channel they were meant for, so the gap can be closed. Without one, an unmapped
		/// channel is logged once - once per distinct channel, not per message - and dropped.
		/// </remarks>
		public string? FallbackChannel { get; set; }

		/// <summary>
		/// Guild the fallback channel belongs to. Defaults to the default guild.
		/// </summary>
		public string? FallbackGuild { get; set; }

		/// <summary>
		/// Channel mappings after validation, keyed by Horde channel id.
		/// </summary>
		[JsonIgnore]
		public IReadOnlyDictionary<string, DiscordDestination> ResolvedChannels { get; private set; }
			= new Dictionary<string, DiscordDestination>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// The catch-all destination, if one is configured and valid.
		/// </summary>
		[JsonIgnore]
		public DiscordDestination? ResolvedFallback { get; private set; }

		/// <summary>
		/// Guild used by anything that does not name one.
		/// </summary>
		[JsonIgnore]
		public string? ResolvedDefaultGuildId { get; private set; }

		/// <inheritdoc/>
		public void PostLoad(PluginConfigOptions configOptions)
		{
			ILogger? logger = configOptions.Logger;

			// Deliberately does not throw. A bad Discord mapping should cost Discord notifications, not fail the
			// whole server's config reload and take Horde's other plugins down with it.
			Dictionary<string, string> guilds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			foreach ((string name, string guildId) in Guilds)
			{
				if (DiscordChannelIds.IsDiscordSnowflake(guildId))
				{
					guilds[name] = guildId;
				}
				else
				{
					logger?.LogError("Discord guild '{Name}' is set to '{GuildId}', which is not a guild id.", name, guildId);
				}
			}

			string? defaultGuildId = ResolveGuild(DefaultGuild, guilds, logger, "defaultGuild")
				?? (guilds.Count == 1 ? guilds.Values.First() : null);

			Dictionary<string, DiscordDestination> resolved = new Dictionary<string, DiscordDestination>(StringComparer.OrdinalIgnoreCase);

			foreach ((string hordeChannel, DiscordChannelMapping mapping) in Channels)
			{
				if (!DiscordChannelIds.IsDiscordSnowflake(mapping.Channel))
				{
					logger?.LogError("Discord channel mapping for '{HordeChannel}': {Problem}",
						hordeChannel, DiscordChannelIds.DescribeIfNotDiscordChannel(mapping.Channel));
					continue;
				}

				// Permissive: Horde carries a Slack channel id for most settings but a bare channel *name* for
				// jobNotificationChannel and updateStreamsNotificationChannel, and a name is indistinguishable from
				// a typo. Only the two unambiguous mistakes are reported.
				string? keyProblem = DiscordChannelIds.DescribeIfNotHordeChannel(hordeChannel);

				if (keyProblem != null)
				{
					logger?.LogWarning("Discord channel mapping key: {Problem}", keyProblem);
				}

				resolved[hordeChannel] = new DiscordDestination(
					mapping.Channel,
					ResolveGuild(mapping.Guild, guilds, logger, $"channel '{hordeChannel}'") ?? defaultGuildId,
					mapping.Label ?? hordeChannel,
					hordeChannel);
			}

			ResolvedChannels = resolved;
			ResolvedDefaultGuildId = defaultGuildId;
			ResolvedFallback = BuildFallback(defaultGuildId, guilds, logger);
		}

		DiscordDestination? BuildFallback(string? defaultGuildId, Dictionary<string, string> guilds, ILogger? logger)
		{
			if (String.IsNullOrEmpty(FallbackChannel))
			{
				return null;
			}

			if (!DiscordChannelIds.IsDiscordSnowflake(FallbackChannel))
			{
				logger?.LogError("Discord fallbackChannel: {Problem}", DiscordChannelIds.DescribeIfNotDiscordChannel(FallbackChannel));
				return null;
			}

			return new DiscordDestination(
				FallbackChannel,
				ResolveGuild(FallbackGuild, guilds, logger, "fallbackGuild") ?? defaultGuildId,
				"fallback",
				IsFallback: true);
		}

		static string? ResolveGuild(string? name, Dictionary<string, string> guilds, ILogger? logger, string context)
		{
			if (String.IsNullOrEmpty(name))
			{
				return null;
			}

			if (guilds.TryGetValue(name, out string? guildId))
			{
				return guildId;
			}

			logger?.LogError("Discord {Context} names guild '{Guild}', which is not in the guilds map.", context, name);
			return null;
		}
	}
}
