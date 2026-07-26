// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

namespace HordeServer
{
	/// <summary>
	/// Which Discord role stands in for one of Horde's user-group handles.
	/// </summary>
	/// <remarks>
	/// An object rather than a bare snowflake for one reason: **a role id only means anything inside its own
	/// guild.** Mentioning a role from another guild does not fail - it renders as the raw <c>&lt;@&amp;id&gt;</c>
	/// text, which pings nobody and looks like a bug. Naming the guild lets a mention be skipped rather than
	/// rendered wrong.
	///
	/// Leave <see cref="Guild"/> unset in the ordinary single-guild case, where there is nothing to be ambiguous
	/// about.
	/// </remarks>
	public class DiscordRoleMapping
	{
		/// <summary>
		/// Human-readable name for the role.
		/// </summary>
		/// <remarks>
		/// Documentation, not data - nothing routes on it. Same reasoning as
		/// <see cref="DiscordChannelMapping.Label"/>: both sides of the mapping are opaque ids.
		/// </remarks>
		public string? Label { get; set; }

		/// <summary>
		/// Key into the <c>guilds</c> map. Unset means the role may be mentioned in any guild.
		/// </summary>
		/// <remarks>
		/// Unset is right for a single-guild install and increasingly wrong as guilds are added, because the same
		/// Horde alias may need a different role in each. Nothing forces it, since an alias that only ever triages
		/// into one guild is fine either way.
		/// </remarks>
		public string? Guild { get; set; }

		/// <summary>
		/// Discord role snowflake to mention.
		/// </summary>
		public string Role { get; set; } = String.Empty;
	}

	/// <summary>
	/// A validated role mapping.
	/// </summary>
	/// <param name="RoleId">Role snowflake.</param>
	/// <param name="GuildId">Guild it belongs to, or null if it may be mentioned anywhere.</param>
	public sealed record DiscordRole(string RoleId, string? GuildId)
	{
		/// <summary>
		/// Whether this role can be mentioned in the given guild.
		/// </summary>
		/// <param name="guildId">Guild the message is going to. Null means the destination's guild is unknown.</param>
		public bool UsableIn(string? guildId)
			=> GuildId == null || guildId == null || String.Equals(GuildId, guildId, StringComparison.Ordinal);

		/// <summary>
		/// The role as Discord renders a mention of it.
		/// </summary>
		public string Mention => $"<@&{RoleId}>";
	}
}
