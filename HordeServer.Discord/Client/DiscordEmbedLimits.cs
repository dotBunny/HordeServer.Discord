// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

namespace HordeServer.Discord.Client
{
	/// <summary>
	/// Size limits Discord enforces on messages and embeds.
	/// </summary>
	/// <remarks>
	/// These are hard 400s, not guidance, and Horde feeds unbounded input into them - log excerpts, error lists, step
	/// names from a stream nobody has pruned in two years. Every builder in this namespace clamps to these rather
	/// than hoping.
	///
	/// Verified against <c>docs.discord.com</c> on 2026-07-25.
	///
	/// <see cref="CombinedEmbedCharacters"/> is the one that bites: it sums title, description, field names, field
	/// values, footer text and author name across *every* embed in the message, so a payload where each individual
	/// value is legal can still be rejected.
	/// </remarks>
	public static class DiscordEmbedLimits
	{
		/// <summary>Maximum characters in an embed title.</summary>
		public const int Title = 256;

		/// <summary>Maximum characters in an embed description.</summary>
		public const int Description = 4096;

		/// <summary>Maximum characters in a field name.</summary>
		public const int FieldName = 256;

		/// <summary>Maximum characters in a field value.</summary>
		public const int FieldValue = 1024;

		/// <summary>Maximum characters in footer text.</summary>
		public const int FooterText = 2048;

		/// <summary>Maximum characters in an author name.</summary>
		public const int AuthorName = 256;

		/// <summary>Maximum fields in one embed.</summary>
		public const int FieldsPerEmbed = 25;

		/// <summary>Maximum embeds in one message.</summary>
		public const int EmbedsPerMessage = 10;

		/// <summary>Maximum characters in the plain content of a message, outside its embeds.</summary>
		public const int MessageContent = 2000;

		/// <summary>Maximum characters summed across every embed in a message.</summary>
		public const int CombinedEmbedCharacters = 6000;

		/// <summary>
		/// Appended to anything that had to be cut short.
		/// </summary>
		/// <remarks>
		/// Truncation is always visible. A message that quietly drops the end of a log excerpt is worse than a short
		/// one, because the reader has no way to know they are looking at part of the story.
		/// </remarks>
		public const string TruncationMarker = "…";

		/// <summary>
		/// Clamps a string to a limit, marking it when anything was removed.
		/// </summary>
		/// <param name="value">Text to clamp.</param>
		/// <param name="maxLength">Limit to clamp to.</param>
		/// <returns>The original string, or a shortened one ending in <see cref="TruncationMarker"/>.</returns>
		public static string Truncate(string value, int maxLength)
		{
			if (maxLength <= 0)
			{
				return String.Empty;
			}

			if (value.Length <= maxLength)
			{
				return value;
			}

			int cut = maxLength - TruncationMarker.Length;

			if (cut <= 0)
			{
				return TruncationMarker[..maxLength];
			}

			// Never cut between the halves of a surrogate pair - an emoji sliced down the middle is not a character,
			// and Discord rejects the payload rather than rendering it oddly.
			if (Char.IsHighSurrogate(value[cut - 1]))
			{
				cut--;
			}

			return String.Concat(value.AsSpan(0, cut), TruncationMarker);
		}
	}
}
