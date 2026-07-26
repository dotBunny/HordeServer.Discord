// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

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
		/// Map of the user-group handle Horde pings to the Discord role that stands in for it.
		/// </summary>
		/// <remarks>
		/// Horde's workflows name a Slack alias to ping when nobody is assigned to an issue - <c>triageAlias</c>,
		/// <c>escalateAlias</c> and the per-issue-type <c>triageTypeAliases</c>. Slack renders those as a user-group
		/// mention; the Discord equivalent is a role mention, so this is the same translation the <see cref="Channels"/>
		/// map performs, on the other axis. Keys are whatever Horde carries.
		///
		/// Each entry may name a guild, because **a role id only means anything inside its own guild** - see
		/// <see cref="DiscordRoleMapping"/>. Leave it unset in a single-guild install.
		/// </remarks>
		public Dictionary<string, DiscordRoleMapping> Roles { get; set; } = new Dictionary<string, DiscordRoleMapping>(StringComparer.OrdinalIgnoreCase);

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
		/// User mappings after validation, keyed by email address.
		/// </summary>
		[JsonIgnore]
		public IReadOnlyDictionary<string, string> ResolvedUsers { get; private set; }
			= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Role mappings after validation, keyed by the handle Horde carries.
		/// </summary>
		/// <remarks>
		/// The guild is resolved to a snowflake here, or left null for a role that may be mentioned anywhere.
		/// </remarks>
		[JsonIgnore]
		public IReadOnlyDictionary<string, DiscordRole> ResolvedRoles { get; private set; }
			= new Dictionary<string, DiscordRole>(StringComparer.OrdinalIgnoreCase);

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
			ResolvedUsers = ResolveSnowflakes(UserMap, "user", logger, IsProbablyEmail);
			ResolvedRoles = ResolveRoles(guilds, logger);
		}

		/// <summary>
		/// Validates the role map and resolves each entry's guild.
		/// </summary>
		/// <remarks>
		/// A role whose guild names nothing in the <c>guilds</c> map is dropped rather than treated as global. The
		/// alternative would mention it in guilds it does not belong to, which renders as raw text and pings nobody
		/// - a failure that looks like a formatting bug rather than a configuration one.
		/// </remarks>
		Dictionary<string, DiscordRole> ResolveRoles(Dictionary<string, string> guilds, ILogger? logger)
		{
			Dictionary<string, DiscordRole> resolved = new Dictionary<string, DiscordRole>(StringComparer.OrdinalIgnoreCase);

			foreach ((string alias, DiscordRoleMapping mapping) in Roles)
			{
				if (!DiscordChannelIds.IsDiscordSnowflake(mapping.Role))
				{
					logger?.LogError("Discord role mapping for '{Alias}' is set to '{Role}', which is not a role id.",
						alias, mapping.Role);
					continue;
				}

				string? guildId = null;

				if (mapping.Guild != null)
				{
					guildId = ResolveGuild(mapping.Guild, guilds, logger, $"role '{alias}'");

					if (guildId == null)
					{
						continue;
					}
				}

				resolved[alias] = new DiscordRole(mapping.Role, guildId);
			}

			return resolved;
		}

		/// <summary>
		/// Keeps the entries of a map whose values are usable Discord snowflakes.
		/// </summary>
		/// <remarks>
		/// A bad entry is dropped and named rather than kept, so that the resolver's "no mapping for this person"
		/// path - which degrades to a plain-text name - is the only way an entry can fail to work. Half-valid state
		/// that produces a mention nobody receives would be much harder to spot.
		/// </remarks>
		/// <param name="map">Configured map.</param>
		/// <param name="what">What the map holds, for diagnostics.</param>
		/// <param name="logger">Logger to report problems to.</param>
		/// <param name="checkKey">Optional check on the key, reported as a warning without dropping the entry.</param>
		/// <returns>The entries that resolved.</returns>
		static IReadOnlyDictionary<string, string> ResolveSnowflakes(Dictionary<string, string> map, string what, ILogger? logger, Func<string, bool>? checkKey)
		{
			Dictionary<string, string> resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			foreach ((string key, string value) in map)
			{
				if (!DiscordChannelIds.IsDiscordSnowflake(value))
				{
					logger?.LogError("Discord {What} mapping for '{Key}' is set to '{Value}', which is not a Discord id.", what, key, value);
					continue;
				}

				if (checkKey != null && !checkKey(key))
				{
					logger?.LogWarning("Discord {What} mapping key '{Key}' does not look like an email address; Horde "
						+ "matches these against the address on the user's Horde account.", what, key);
				}

				resolved[key] = value;
			}

			return resolved;
		}

		/// <summary>
		/// Whether a user map key looks like the thing Horde will match it against.
		/// </summary>
		/// <remarks>
		/// Deliberately shallow. The only mistake worth catching is a name or a Horde user id put where an email
		/// belongs, and anything stricter would start rejecting addresses that are perfectly valid.
		/// </remarks>
		/// <param name="key">Configured key.</param>
		/// <returns>True if it could be an email address.</returns>
		static bool IsProbablyEmail(string key) => key.Contains('@', StringComparison.Ordinal);

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
