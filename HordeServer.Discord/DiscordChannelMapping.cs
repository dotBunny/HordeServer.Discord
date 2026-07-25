// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

namespace HordeServer
{
	/// <summary>
	/// Where one of Horde's channels lands in Discord.
	/// </summary>
	/// <remarks>
	/// Keyed elsewhere by the Slack channel id Horde already carries, so this only has to say where that goes.
	/// </remarks>
	public class DiscordChannelMapping
	{
		/// <summary>
		/// Human-readable name for the channel.
		/// </summary>
		/// <remarks>
		/// Documentation, not data - nothing routes on it. Both sides of a mapping are opaque ids, so without a
		/// label nobody reading the config can tell which channel an entry is about. It is also what shows up in
		/// the log when delivery fails.
		/// </remarks>
		public string? Label { get; set; }

		/// <summary>
		/// Key into the <c>guilds</c> map. Defaults to the default guild.
		/// </summary>
		public string? Guild { get; set; }

		/// <summary>
		/// Discord channel snowflake to post to.
		/// </summary>
		public string Channel { get; set; } = String.Empty;
	}
}
