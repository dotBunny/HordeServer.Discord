// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

namespace HordeServer.Discord.Client
{
	/// <summary>
	/// Builds an embed that Discord will accept.
	/// </summary>
	/// <remarks>
	/// Every setter clamps to <see cref="DiscordEmbedLimits"/> as it goes, so callers can hand over a stack trace or
	/// a hundred failing steps without checking anything first. Over-long values are marked as truncated rather than
	/// silently shortened, and fields past the twenty-fifth are replaced by a count of what was left out - the reader
	/// always knows they are looking at part of the picture.
	/// </remarks>
	public sealed class DiscordEmbedBuilder
	{
		readonly List<DiscordEmbedField> _fields = new List<DiscordEmbedField>();

		string? _title;
		string? _description;
		string? _url;
		int? _color;
		DateTimeOffset? _timestamp;
		DiscordEmbedFooter? _footer;
		DiscordEmbedAuthor? _author;

		/// <summary>
		/// Sets the title.
		/// </summary>
		/// <param name="title">Title text, truncated to fit.</param>
		/// <returns>This builder.</returns>
		public DiscordEmbedBuilder WithTitle(string title)
		{
			_title = DiscordEmbedLimits.Truncate(title, DiscordEmbedLimits.Title);
			return this;
		}

		/// <summary>
		/// Sets the description.
		/// </summary>
		/// <param name="description">Body text, truncated to fit. Discord markdown is supported.</param>
		/// <returns>This builder.</returns>
		public DiscordEmbedBuilder WithDescription(string description)
		{
			_description = DiscordEmbedLimits.Truncate(description, DiscordEmbedLimits.Description);
			return this;
		}

		/// <summary>
		/// Makes the title link somewhere - in practice, always the Horde page for whatever this is about.
		/// </summary>
		/// <param name="url">Target of the link.</param>
		/// <returns>This builder.</returns>
		public DiscordEmbedBuilder WithUrl(string url)
		{
			_url = url;
			return this;
		}

		/// <summary>
		/// Sets the colour of the stripe down the left edge.
		/// </summary>
		/// <param name="color">Packed RGB value.</param>
		/// <returns>This builder.</returns>
		public DiscordEmbedBuilder WithColor(int color)
		{
			_color = color;
			return this;
		}

		/// <summary>
		/// Sets the timestamp shown in the footer.
		/// </summary>
		/// <param name="timestamp">When the thing being reported happened.</param>
		/// <returns>This builder.</returns>
		public DiscordEmbedBuilder WithTimestamp(DateTimeOffset timestamp)
		{
			_timestamp = timestamp;
			return this;
		}

		/// <summary>
		/// Sets the footer.
		/// </summary>
		/// <param name="text">Footer text, truncated to fit.</param>
		/// <param name="iconUrl">Optional icon.</param>
		/// <returns>This builder.</returns>
		public DiscordEmbedBuilder WithFooter(string text, string? iconUrl = null)
		{
			_footer = new DiscordEmbedFooter(DiscordEmbedLimits.Truncate(text, DiscordEmbedLimits.FooterText), iconUrl);
			return this;
		}

		/// <summary>
		/// Sets the author line.
		/// </summary>
		/// <param name="name">Author name, truncated to fit.</param>
		/// <param name="url">Optional link on the name.</param>
		/// <param name="iconUrl">Optional avatar.</param>
		/// <returns>This builder.</returns>
		public DiscordEmbedBuilder WithAuthor(string name, string? url = null, string? iconUrl = null)
		{
			_author = new DiscordEmbedAuthor(DiscordEmbedLimits.Truncate(name, DiscordEmbedLimits.AuthorName), url, iconUrl);
			return this;
		}

		/// <summary>
		/// Adds a field.
		/// </summary>
		/// <remarks>
		/// Never rejects an add. Fields beyond the limit are counted and reported in a final overflow field by
		/// <see cref="Build"/>, because a caller looping over failing steps has no sensible way to handle a refusal
		/// and would most likely just drop them.
		/// </remarks>
		/// <param name="name">Field name, truncated to fit.</param>
		/// <param name="value">Field value, truncated to fit.</param>
		/// <param name="inline">Whether the field shares a row with its neighbours.</param>
		/// <returns>This builder.</returns>
		public DiscordEmbedBuilder AddField(string name, string value, bool inline = false)
		{
			_fields.Add(new DiscordEmbedField(name, value, inline));
			return this;
		}

		/// <summary>
		/// Number of fields added, including any that will be summarised away as overflow.
		/// </summary>
		public int FieldCount => _fields.Count;

		/// <summary>
		/// Characters set aside so there is always room to say what was left out.
		/// </summary>
		/// <remarks>
		/// Held back from the character budget rather than found afterwards. Trying to squeeze the overflow notice in
		/// once the budget is already spent means removing fields to make room, which changes the number the notice
		/// has to report - a small loop with an annoying number of edge cases, bought off here for 64 characters out
		/// of six thousand.
		/// </remarks>
		const int OverflowReserve = 64;

		/// <summary>
		/// Produces the embed.
		/// </summary>
		/// <remarks>
		/// The result respects <see cref="DiscordEmbedLimits.CombinedEmbedCharacters"/> on its own, not just the
		/// individual per-value limits. Those do not imply each other: a legal title, description, footer and
		/// twenty-five legal fields add up to nearly six times the ceiling.
		/// </remarks>
		/// <returns>An embed within every limit Discord enforces on a single embed.</returns>
		public DiscordEmbed Build()
		{
			// The reserve comes off the top, before anything is measured against the budget. Taking it later would
			// let the description alone spend the whole ceiling, leaving no room for the notice that has to say so.
			int budget = DiscordEmbedLimits.CombinedEmbedCharacters - (_fields.Count > 0 ? OverflowReserve : 0);

			string? description = _description;

			// Fixed parts can total 2560 at most, so they always fit. The description is the only piece large enough
			// to break the budget by itself, and the only one where losing the tail is tolerable.
			int used = (_title?.Length ?? 0) + (_footer?.Text.Length ?? 0) + (_author?.Name.Length ?? 0);

			if (description != null && used + description.Length > budget)
			{
				description = DiscordEmbedLimits.Truncate(description, Math.Max(0, budget - used));
			}

			used += description?.Length ?? 0;

			DiscordEmbed embed = new DiscordEmbed
			{
				Title = _title,
				Description = description,
				Url = _url,
				Color = _color,
				Timestamp = _timestamp,
				Footer = _footer,
				Author = _author,
			};

			if (_fields.Count == 0)
			{
				return embed;
			}

			List<DiscordEmbedField> chosen = new List<DiscordEmbedField>();
			int omitted = 0;

			foreach (DiscordEmbedField field in _fields)
			{
				int cost = field.Name.Length + field.Value.Length;

				if (chosen.Count < DiscordEmbedLimits.FieldsPerEmbed && used + cost <= budget)
				{
					chosen.Add(field);
					used += cost;
				}
				else
				{
					omitted++;
				}
			}

			if (omitted > 0)
			{
				// Give up the last slot to say how many were left out. Losing one more real field is a fair price
				// for the reader knowing the list is incomplete.
				if (chosen.Count == DiscordEmbedLimits.FieldsPerEmbed)
				{
					chosen.RemoveAt(chosen.Count - 1);
					omitted++;
				}

				chosen.Add(new DiscordEmbedField(DiscordEmbedLimits.TruncationMarker, $"and {omitted} more"));
			}

			embed.Fields = chosen;
			return embed;
		}
	}
}
