// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

namespace HordeServer.Discord.Client
{
	/// <summary>
	/// Builds a message payload that Discord will accept.
	/// </summary>
	/// <remarks>
	/// Applies the two limits that span a whole message rather than one embed: at most
	/// <see cref="DiscordEmbedLimits.EmbedsPerMessage"/> embeds, and at most
	/// <see cref="DiscordEmbedLimits.CombinedEmbedCharacters"/> characters summed across all of them. Anything that
	/// does not fit is dropped whole, with a line added to the content saying so - a half-rendered embed would be
	/// more confusing than an honest count.
	///
	/// Mentions default to inert. See <see cref="DiscordAllowedMentions"/> for why that is not Discord's default.
	/// </remarks>
	public sealed class DiscordMessageBuilder
	{
		readonly List<DiscordEmbed> _embeds = new List<DiscordEmbed>();

		string? _content;
		DiscordAllowedMentions? _allowedMentions;
		List<DiscordComponent>? _components;

		/// <summary>
		/// Sets the plain text shown above the embeds.
		/// </summary>
		/// <param name="content">Message content, truncated to fit.</param>
		/// <returns>This builder.</returns>
		public DiscordMessageBuilder WithContent(string content)
		{
			_content = content;
			return this;
		}

		/// <summary>
		/// Allows some mentions in the content to actually notify people.
		/// </summary>
		/// <param name="allowedMentions">Policy to apply. Defaults to <see cref="DiscordAllowedMentions.None"/>.</param>
		/// <returns>This builder.</returns>
		public DiscordMessageBuilder WithAllowedMentions(DiscordAllowedMentions allowedMentions)
		{
			_allowedMentions = allowedMentions;
			return this;
		}

		/// <summary>
		/// Adds an embed.
		/// </summary>
		/// <param name="embed">Embed to add.</param>
		/// <returns>This builder.</returns>
		public DiscordMessageBuilder AddEmbed(DiscordEmbed embed)
		{
			_embeds.Add(embed);
			return this;
		}

		/// <summary>
		/// Builds and adds an embed.
		/// </summary>
		/// <param name="embed">Builder to take the embed from.</param>
		/// <returns>This builder.</returns>
		public DiscordMessageBuilder AddEmbed(DiscordEmbedBuilder embed) => AddEmbed(embed.Build());

		/// <summary>
		/// Adds action rows of buttons beneath the embeds.
		/// </summary>
		/// <param name="components">Builder to take the rows from. Nothing is added if it is empty.</param>
		/// <returns>This builder.</returns>
		public DiscordMessageBuilder WithComponents(DiscordComponentBuilder components)
		{
			_components = components.Build();
			return this;
		}

		/// <summary>
		/// Removes every button from a message being edited.
		/// </summary>
		/// <remarks>
		/// An omitted <c>components</c> leaves the existing ones in place, so taking buttons away means sending an
		/// explicitly empty list. This is how an issue that has been resolved stops offering to resolve it again.
		/// </remarks>
		/// <returns>This builder.</returns>
		public DiscordMessageBuilder WithoutComponents()
		{
			_components = new List<DiscordComponent>();
			return this;
		}

		/// <summary>
		/// Produces the message.
		/// </summary>
		/// <returns>A payload within every limit Discord enforces on a message.</returns>
		public DiscordMessage Build()
		{
			List<DiscordEmbed> included = new List<DiscordEmbed>();
			int used = 0;
			int omitted = 0;

			foreach (DiscordEmbed embed in _embeds)
			{
				int cost = embed.CharacterCount;

				if (included.Count < DiscordEmbedLimits.EmbedsPerMessage && used + cost <= DiscordEmbedLimits.CombinedEmbedCharacters)
				{
					included.Add(embed);
					used += cost;
				}
				else
				{
					omitted++;
				}
			}

			string? content = _content;

			if (omitted > 0)
			{
				string notice = $"{DiscordEmbedLimits.TruncationMarker} {omitted} further "
					+ (omitted == 1 ? "section" : "sections") + " omitted to fit Discord's message limits.";

				content = String.IsNullOrEmpty(content)
					? notice
					: DiscordEmbedLimits.Truncate(content, DiscordEmbedLimits.MessageContent - notice.Length - 1) + "\n" + notice;
			}

			return new DiscordMessage
			{
				Content = content == null ? null : DiscordEmbedLimits.Truncate(content, DiscordEmbedLimits.MessageContent),
				Embeds = included.Count > 0 ? included : null,
				AllowedMentions = _allowedMentions ?? DiscordAllowedMentions.None,
				Components = _components,
			};
		}
	}
}
