// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using HordeServer.Plugins;

namespace HordeServer
{
	/// <summary>
	/// Global (hot-reloadable) configuration for the Discord plugin.
	/// </summary>
	/// <remarks>
	/// This is the half of the configuration that changes as people and streams come and go, so it deliberately
	/// lives here rather than in <see cref="DiscordServerConfig"/> - the config system reloads it without a
	/// server restart.
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

		/// <inheritdoc/>
		public void PostLoad(PluginConfigOptions configOptions)
		{
			// Rebuild any derived lookups here once per-stream routing lands in Phase 3.
		}
	}
}
