// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Diagnostics.CodeAnalysis;

namespace HordeServer.Discord.Client
{
	/// <summary>
	/// A link to one message, which is also everything needed to find its thread again.
	/// </summary>
	/// <remarks>
	/// This carries the whole persistent state of issue triage, and it fits in a URL because of one Discord
	/// property: **a thread created from a message has the same id as that message**. So a single
	/// <c>channels/{guild}/{channel}/{message}</c> link yields the channel the parent is in, the message to edit in
	/// place, *and* the thread to post updates into.
	///
	/// That is what let the planned Mongo message-state collection be dropped - Horde's own
	/// <c>IIssue.WorkflowThreadUrl</c> holds one URL per issue, which is exactly one of these. See
	/// <c>.claude/PLAN.md</c> section 3.3.6.
	/// </remarks>
	/// <param name="GuildId">Guild the message is in.</param>
	/// <param name="ChannelId">Channel the message is in.</param>
	/// <param name="MessageId">The message itself, and the id of any thread started from it.</param>
	public sealed record DiscordMessageLink(string GuildId, string ChannelId, string MessageId)
	{
		/// <summary>
		/// Host every Discord message link uses.
		/// </summary>
		public const string Host = "discord.com";

		/// <summary>
		/// The thread started from this message, whose id Discord makes equal to the message's own.
		/// </summary>
		public string ThreadId => MessageId;

		/// <summary>
		/// The message, as something that can be edited.
		/// </summary>
		public DiscordMessageReference Reference => new DiscordMessageReference(ChannelId, MessageId);

		/// <summary>
		/// Renders the link.
		/// </summary>
		public override string ToString() => $"https://{Host}/channels/{GuildId}/{ChannelId}/{MessageId}";

		/// <summary>
		/// Builds the link for a posted message.
		/// </summary>
		/// <param name="guildId">Guild it was posted in.</param>
		/// <param name="reference">The posted message.</param>
		public static DiscordMessageLink For(string guildId, DiscordMessageReference reference)
			=> new DiscordMessageLink(guildId, reference.ChannelId, reference.MessageId);

		/// <summary>
		/// Reads a link back, if it is one of ours.
		/// </summary>
		/// <remarks>
		/// Returns false for anything that is not a Discord message link, which is the important case rather than an
		/// edge one: the field this is read from may well hold a Slack permalink put there by the other sink, and
		/// mistaking one for a thread id would send triage updates into nowhere.
		/// </remarks>
		/// <param name="uri">Value to read.</param>
		/// <param name="link">The parsed link.</param>
		/// <returns>False if it is not a Discord message link.</returns>
		public static bool TryParse(Uri? uri, [NotNullWhen(true)] out DiscordMessageLink? link)
		{
			link = null;

			if (uri == null || !uri.Host.EndsWith(Host, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

			// channels/{guild}/{channel}/{message}. A three-segment link addresses a channel rather than a message
			// and has no thread behind it.
			if (segments.Length != 4 || !segments[0].Equals("channels", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			if (!IsSnowflake(segments[1]) || !IsSnowflake(segments[2]) || !IsSnowflake(segments[3]))
			{
				return false;
			}

			link = new DiscordMessageLink(segments[1], segments[2], segments[3]);
			return true;
		}

		static bool IsSnowflake(string value) => value.Length > 0 && value.All(Char.IsAsciiDigit);
	}
}
