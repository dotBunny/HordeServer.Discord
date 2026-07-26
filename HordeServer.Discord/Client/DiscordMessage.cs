// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace HordeServer.Discord.Client
{
	/// <summary>
	/// Payload for creating or editing a Discord message.
	/// </summary>
	/// <remarks>Build these with <see cref="DiscordMessageBuilder"/>, which applies the per-message limits.</remarks>
	public sealed class DiscordMessage
	{
		/// <summary>Plain text above the embeds. Optional when there is at least one embed.</summary>
		[JsonPropertyName("content")]
		public string? Content { get; set; }

		/// <summary>Rich embeds. At most <see cref="DiscordEmbedLimits.EmbedsPerMessage"/>.</summary>
		[JsonPropertyName("embeds")]
		public List<DiscordEmbed>? Embeds { get; set; }

		/// <summary>Which mentions in the content are allowed to actually ping anyone.</summary>
		[JsonPropertyName("allowed_mentions")]
		public DiscordAllowedMentions? AllowedMentions { get; set; }

		/// <summary>
		/// Action rows of buttons beneath the embeds.
		/// </summary>
		/// <remarks>
		/// Sending this on an *edit* replaces whatever was there, and omitting it leaves the existing components
		/// alone. An empty list is therefore the only way to take buttons away - which is how a triage message stops
		/// offering actions once the issue is resolved.
		/// </remarks>
		[JsonPropertyName("components")]
		public List<DiscordComponent>? Components { get; set; }

		/// <summary>Bit flags. See <see cref="DiscordMessageFlags"/>.</summary>
		[JsonPropertyName("flags")]
		public int? Flags { get; set; }
	}

	/// <summary>
	/// Message flags, as a bit field.
	/// </summary>
	public static class DiscordMessageFlags
	{
		/// <summary>
		/// Visible only to the person whose interaction produced it.
		/// </summary>
		/// <remarks>
		/// Only meaningful on an interaction response - an ordinary post has nobody to be ephemeral to. Discord
		/// keeps no lasting record of one, so it cannot be edited later by message id and never appears in channel
		/// history.
		/// </remarks>
		public const int Ephemeral = 1 << 6;
	}

	/// <summary>
	/// Controls which mentions in a message notify anyone.
	/// </summary>
	/// <remarks>
	/// Discord's default is to honour every mention it can parse out of the content, including <c>@everyone</c>. A
	/// build system reproducing arbitrary strings - step names, error text, commit descriptions - must not inherit
	/// that default, so <see cref="DiscordMessageBuilder"/> sends <see cref="None"/> unless a caller has deliberately
	/// asked to ping specific users.
	/// </remarks>
	public sealed class DiscordAllowedMentions
	{
		/// <summary>
		/// Mention categories to honour. Empty means none, which is not the same as omitting the property.
		/// </summary>
		[JsonPropertyName("parse")]
		public List<string> Parse { get; set; } = new List<string>();

		/// <summary>
		/// Specific users allowed to be pinged, whatever <see cref="Parse"/> says.
		/// </summary>
		[JsonPropertyName("users")]
		public List<string>? Users { get; set; }

		/// <summary>
		/// Renders every mention inert.
		/// </summary>
		public static DiscordAllowedMentions None { get; } = new DiscordAllowedMentions();

		/// <summary>
		/// Allows exactly the listed users to be pinged, and nobody else.
		/// </summary>
		/// <param name="userIds">User snowflakes to allow.</param>
		/// <returns>An allowed-mentions policy naming just those users.</returns>
		public static DiscordAllowedMentions ForUsers(IEnumerable<string> userIds)
			=> new DiscordAllowedMentions { Users = userIds.ToList() };
	}
}
