// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace HordeServer.Discord.Client
{
	/// <summary>
	/// A rich embed attached to a Discord message.
	/// </summary>
	/// <remarks>
	/// The wire shape, not a friendly API - build these with <see cref="DiscordEmbedBuilder"/>, which is what applies
	/// <see cref="DiscordEmbedLimits"/>. Only the subset of fields the plugin sends is modelled; Discord ignores what
	/// it is not given, and unused properties would just be untested surface.
	/// </remarks>
	public sealed class DiscordEmbed
	{
		/// <summary>Title line, rendered bold at the top of the embed.</summary>
		[JsonPropertyName("title")]
		public string? Title { get; set; }

		/// <summary>Body text. Supports Discord's markdown subset.</summary>
		[JsonPropertyName("description")]
		public string? Description { get; set; }

		/// <summary>Makes the title a link.</summary>
		[JsonPropertyName("url")]
		public string? Url { get; set; }

		/// <summary>Timestamp shown in the footer.</summary>
		[JsonPropertyName("timestamp")]
		public DateTimeOffset? Timestamp { get; set; }

		/// <summary>Colour of the stripe down the left edge, as a packed RGB integer.</summary>
		[JsonPropertyName("color")]
		public int? Color { get; set; }

		/// <summary>Footer, shown small beneath the fields.</summary>
		[JsonPropertyName("footer")]
		public DiscordEmbedFooter? Footer { get; set; }

		/// <summary>Author line, shown above the title.</summary>
		[JsonPropertyName("author")]
		public DiscordEmbedAuthor? Author { get; set; }

		/// <summary>Name/value pairs, rendered in one to three columns.</summary>
		[JsonPropertyName("fields")]
		public List<DiscordEmbedField>? Fields { get; set; }

		/// <summary>
		/// Characters this embed contributes to the per-message combined ceiling.
		/// </summary>
		/// <remarks>
		/// Counts exactly what Discord counts - title, description, field names and values, footer text and author
		/// name. URLs, colours and timestamps are free.
		/// </remarks>
		[JsonIgnore]
		public int CharacterCount
		{
			get
			{
				int count = (Title?.Length ?? 0) + (Description?.Length ?? 0)
					+ (Footer?.Text?.Length ?? 0) + (Author?.Name?.Length ?? 0);

				if (Fields != null)
				{
					// Not named 'field': C# 14 made that a contextual keyword inside a property accessor.
					foreach (DiscordEmbedField embedField in Fields)
					{
						count += embedField.Name.Length + embedField.Value.Length;
					}
				}

				return count;
			}
		}
	}

	/// <summary>
	/// A name/value pair inside an embed.
	/// </summary>
	public sealed class DiscordEmbedField
	{
		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="name">Field name. Truncated to <see cref="DiscordEmbedLimits.FieldName"/>.</param>
		/// <param name="value">Field value. Truncated to <see cref="DiscordEmbedLimits.FieldValue"/>.</param>
		/// <param name="inline">Whether the field shares a row with its neighbours.</param>
		public DiscordEmbedField(string name, string value, bool inline = false)
		{
			Name = DiscordEmbedLimits.Truncate(name, DiscordEmbedLimits.FieldName);
			Value = DiscordEmbedLimits.Truncate(value, DiscordEmbedLimits.FieldValue);
			Inline = inline;
		}

		/// <summary>Field name.</summary>
		[JsonPropertyName("name")]
		public string Name { get; }

		/// <summary>Field value.</summary>
		[JsonPropertyName("value")]
		public string Value { get; }

		/// <summary>Whether the field shares a row with its neighbours. Three fit across.</summary>
		[JsonPropertyName("inline")]
		public bool Inline { get; }
	}

	/// <summary>
	/// Footer of an embed.
	/// </summary>
	/// <param name="Text">Footer text.</param>
	/// <param name="IconUrl">Optional icon shown beside the text.</param>
	public sealed record DiscordEmbedFooter(
		[property: JsonPropertyName("text")] string Text,
		[property: JsonPropertyName("icon_url")] string? IconUrl = null);

	/// <summary>
	/// Author line of an embed.
	/// </summary>
	/// <param name="Name">Author name.</param>
	/// <param name="Url">Optional link on the name.</param>
	/// <param name="IconUrl">Optional avatar shown beside the name.</param>
	public sealed record DiscordEmbedAuthor(
		[property: JsonPropertyName("name")] string Name,
		[property: JsonPropertyName("url")] string? Url = null,
		[property: JsonPropertyName("icon_url")] string? IconUrl = null);
}
