// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace HordeServer.Discord.Client
{
	/// <summary>
	/// Component types, as they appear in the <c>type</c> field.
	/// </summary>
	public static class DiscordComponentType
	{
		/// <summary>A row holding up to five buttons, or one select menu.</summary>
		public const int ActionRow = 1;

		/// <summary>A button.</summary>
		public const int Button = 2;

		/// <summary>A dropdown of pre-set options.</summary>
		public const int StringSelect = 3;

		/// <summary>A single-line or paragraph text box. Modals only.</summary>
		public const int TextInput = 4;
	}

	/// <summary>
	/// Button styles.
	/// </summary>
	/// <remarks>
	/// Style is the only signal a button has for how consequential it is, since Discord renders no icon of its own.
	/// Triage uses <see cref="Success"/> for accepting, <see cref="Danger"/> for declining, and
	/// <see cref="Secondary"/> for everything reversible.
	/// </remarks>
	public static class DiscordButtonStyle
	{
		/// <summary>Blurple. The primary action of the message.</summary>
		public const int Primary = 1;

		/// <summary>Grey. Everything unremarkable.</summary>
		public const int Secondary = 2;

		/// <summary>Green.</summary>
		public const int Success = 3;

		/// <summary>Red.</summary>
		public const int Danger = 4;

		/// <summary>A link. Carries a URL instead of a custom id, and never produces an interaction.</summary>
		public const int Link = 5;
	}

	/// <summary>
	/// Limits Discord enforces on message components.
	/// </summary>
	/// <remarks>
	/// Same reasoning as <see cref="DiscordEmbedLimits"/>: these are 400s. <see cref="CustomId"/> is the one that
	/// constrains design rather than formatting - every piece of state a button carries has to fit in it, because a
	/// component interaction arrives with nothing else identifying what was clicked.
	///
	/// Verified against <c>docs.discord.com</c> on 2026-07-26.
	/// </remarks>
	public static class DiscordComponentLimits
	{
		/// <summary>Maximum action rows on one message.</summary>
		public const int RowsPerMessage = 5;

		/// <summary>Maximum buttons in one action row.</summary>
		public const int ButtonsPerRow = 5;

		/// <summary>Maximum characters in a button label.</summary>
		public const int ButtonLabel = 80;

		/// <summary>Maximum characters in a custom id.</summary>
		public const int CustomId = 100;
	}

	/// <summary>
	/// A message component, in the shape Discord serialises them.
	/// </summary>
	/// <remarks>
	/// One type with nullable members rather than a hierarchy, because that is what the wire format is: every
	/// component is a <c>type</c> plus whichever fields that type uses. Modelling it as a class per component would
	/// need a polymorphic converter to gain nothing - these objects are built by
	/// <see cref="DiscordComponentBuilder"/> and read by Discord.
	/// </remarks>
	public sealed class DiscordComponent
	{
		/// <summary>Which kind of component this is. See <see cref="DiscordComponentType"/>.</summary>
		[JsonPropertyName("type")]
		public int Type { get; set; }

		/// <summary>Children, for an action row.</summary>
		[JsonPropertyName("components")]
		public List<DiscordComponent>? Components { get; set; }

		/// <summary>Button style. See <see cref="DiscordButtonStyle"/>.</summary>
		[JsonPropertyName("style")]
		public int? Style { get; set; }

		/// <summary>Text on the button.</summary>
		[JsonPropertyName("label")]
		public string? Label { get; set; }

		/// <summary>Identifier echoed back when the component is used. Absent on link buttons.</summary>
		[JsonPropertyName("custom_id")]
		public string? CustomId { get; set; }

		/// <summary>Target of a link button.</summary>
		[JsonPropertyName("url")]
		public string? Url { get; set; }

		/// <summary>Whether the component is greyed out and unusable.</summary>
		[JsonPropertyName("disabled")]
		public bool? Disabled { get; set; }
	}

	/// <summary>
	/// Builds the action rows for a message.
	/// </summary>
	/// <remarks>
	/// Rows are filled left to right and wrap at <see cref="DiscordComponentLimits.ButtonsPerRow"/>, so callers add
	/// buttons and do not think about layout. Anything past the fifth row is dropped rather than sent, for the same
	/// reason the embed builder drops overflow: a rejected message delivers nothing at all, and a triage message
	/// missing its least important button still triages.
	/// </remarks>
	public sealed class DiscordComponentBuilder
	{
		readonly List<DiscordComponent> _buttons = new List<DiscordComponent>();

		/// <summary>
		/// Whether anything has been added.
		/// </summary>
		public bool IsEmpty => _buttons.Count == 0;

		/// <summary>
		/// Adds a button that produces an interaction when pressed.
		/// </summary>
		/// <param name="customId">Identifier echoed back on press. Truncation would break the round trip, so an
		/// over-long id is rejected rather than shortened.</param>
		/// <param name="label">Text on the button, truncated to fit.</param>
		/// <param name="style">One of <see cref="DiscordButtonStyle"/>.</param>
		/// <param name="disabled">Whether it starts greyed out.</param>
		/// <returns>This builder.</returns>
		/// <exception cref="ArgumentException">The custom id is empty or too long.</exception>
		public DiscordComponentBuilder AddButton(string customId, string label, int style = DiscordButtonStyle.Secondary, bool disabled = false)
		{
			if (String.IsNullOrEmpty(customId))
			{
				throw new ArgumentException("A button needs a custom id, or it cannot be acted on.", nameof(customId));
			}

			// Deliberately not truncated. Everything else here clamps to fit, but a custom id is the only thing
			// identifying what was pressed: a shortened one comes back as a verb nothing recognises, which is a
			// silent no-op rather than a visible error.
			if (customId.Length > DiscordComponentLimits.CustomId)
			{
				throw new ArgumentException(
					$"Custom id '{customId}' is {customId.Length} characters; Discord allows {DiscordComponentLimits.CustomId}.",
					nameof(customId));
			}

			_buttons.Add(new DiscordComponent
			{
				Type = DiscordComponentType.Button,
				Style = style,
				Label = DiscordEmbedLimits.Truncate(label, DiscordComponentLimits.ButtonLabel),
				CustomId = customId,
				Disabled = disabled ? true : null,
			});

			return this;
		}

		/// <summary>
		/// Adds a button that opens a URL.
		/// </summary>
		/// <remarks>
		/// A link button never calls back, which makes it the right way to offer "open this in Horde" beside the
		/// actions that do - no interaction, no deadline, and it keeps working when the gateway is down.
		/// </remarks>
		/// <param name="url">Where it goes.</param>
		/// <param name="label">Text on the button, truncated to fit.</param>
		/// <returns>This builder.</returns>
		public DiscordComponentBuilder AddLink(string url, string label)
		{
			_buttons.Add(new DiscordComponent
			{
				Type = DiscordComponentType.Button,
				Style = DiscordButtonStyle.Link,
				Label = DiscordEmbedLimits.Truncate(label, DiscordComponentLimits.ButtonLabel),
				Url = url,
			});

			return this;
		}

		/// <summary>
		/// Lays the buttons out into rows.
		/// </summary>
		/// <returns>The action rows, or null if there is nothing to show.</returns>
		public List<DiscordComponent>? Build()
		{
			if (_buttons.Count == 0)
			{
				return null;
			}

			List<DiscordComponent> rows = new List<DiscordComponent>();

			foreach (DiscordComponent button in _buttons)
			{
				if (rows.Count == 0 || rows[^1].Components!.Count == DiscordComponentLimits.ButtonsPerRow)
				{
					if (rows.Count == DiscordComponentLimits.RowsPerMessage)
					{
						break;
					}

					rows.Add(new DiscordComponent
					{
						Type = DiscordComponentType.ActionRow,
						Components = new List<DiscordComponent>(),
					});
				}

				rows[^1].Components!.Add(button);
			}

			return rows;
		}
	}
}
