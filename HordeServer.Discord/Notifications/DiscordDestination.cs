// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

namespace HordeServer.Discord.Notifications
{
	/// <summary>
	/// A resolved place to post.
	/// </summary>
	/// <remarks>
	/// <see cref="GuildId"/> is not needed to post - <c>POST /channels/{id}/messages</c> takes only the channel, and
	/// snowflakes are globally unique. It is carried anyway because three things later on cannot work without it:
	/// a bot may only DM someone it shares a guild with, interactions and slash commands register per guild, and
	/// startup validation wants to check the bot can actually see the channel. Keeping the guild off the posting
	/// path is also what makes supporting more than one of them additive rather than a rewrite.
	/// </remarks>
	/// <param name="ChannelId">Discord channel snowflake to post to.</param>
	/// <param name="GuildId">Guild the channel belongs to, when configuration says which.</param>
	/// <param name="Label">Human-readable name for logs and diagnostics.</param>
	/// <param name="SourceChannel">Horde channel id this was resolved from, if it came from the mapping table.</param>
	/// <param name="IsFallback">Whether this is the catch-all rather than a channel that was actually mapped.</param>
	public sealed record DiscordDestination(
		string ChannelId,
		string? GuildId = null,
		string? Label = null,
		string? SourceChannel = null,
		bool IsFallback = false)
	{
		/// <inheritdoc/>
		public override string ToString()
			=> Label == null ? ChannelId : $"{Label} ({ChannelId})";
	}
}
