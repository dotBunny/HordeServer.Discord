// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Text;

namespace HordeServer.Discord.Client
{
	/// <summary>
	/// Helpers for Discord's markdown dialect.
	/// </summary>
	public static class DiscordMarkdown
	{
		static readonly SearchValues<char> s_reserved = SearchValues.Create("*_`~|\\[]<>");

		/// <summary>
		/// Escapes text that came from somewhere else, so it renders as written.
		/// </summary>
		/// <remarks>
		/// Step names, job names and compiler output are full of underscores, asterisks and backticks. Left alone
		/// they turn half an error message italic, swallow it into a code fence that never closes, or - with square
		/// and angle brackets - collide with the link and mention syntax the surrounding message is built from.
		/// </remarks>
		/// <param name="text">Text to escape.</param>
		/// <returns>The text with Discord's markdown characters escaped.</returns>
		public static string Escape(string text)
		{
			if (!text.AsSpan().ContainsAny(s_reserved))
			{
				return text;
			}

			StringBuilder builder = new StringBuilder(text.Length + 8);

			foreach (char character in text)
			{
				if (s_reserved.Contains(character))
				{
					builder.Append('\\');
				}

				builder.Append(character);
			}

			return builder.ToString();
		}
	}
}
