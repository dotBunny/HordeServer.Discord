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

		/// <summary>Maximum options in one select menu.</summary>
		public const int SelectOptions = 25;

		/// <summary>Maximum characters in a select option's label.</summary>
		public const int SelectOptionLabel = 100;

		/// <summary>Maximum characters in a select menu's placeholder.</summary>
		public const int SelectPlaceholder = 150;

		/// <summary>Maximum characters in a text input's label.</summary>
		/// <remarks>Notably shorter than an embed's - 45, not 256. Long field names have to be abbreviated.</remarks>
		public const int TextInputLabel = 45;

		/// <summary>Maximum characters a text input will accept.</summary>
		public const int TextInputValue = 4000;

		/// <summary>Maximum text inputs in one modal.</summary>
		/// <remarks>
		/// The number that forced the hybrid Mark Fixed flow. Slack's equivalent view presents seven inputs, three of
		/// them non-text. See <c>.claude/PLAN.md</c> section 3.3.4.
		/// </remarks>
		public const int TextInputsPerModal = 5;
	}

	/// <summary>
	/// Text input styles.
	/// </summary>
	public static class DiscordTextInputStyle
	{
		/// <summary>A single line.</summary>
		public const int Short = 1;

		/// <summary>A resizable box.</summary>
		public const int Paragraph = 2;
	}

	/// <summary>
	/// One choice in a select menu.
	/// </summary>
	public sealed class DiscordSelectOption
	{
		/// <summary>What the reader sees.</summary>
		[JsonPropertyName("label")]
		public string? Label { get; set; }

		/// <summary>What comes back when it is chosen.</summary>
		[JsonPropertyName("value")]
		public string? Value { get; set; }

		/// <summary>Smaller text beneath the label.</summary>
		[JsonPropertyName("description")]
		public string? Description { get; set; }

		/// <summary>Whether it starts selected.</summary>
		[JsonPropertyName("default")]
		public bool? Default { get; set; }
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

		/// <summary>Choices, for a select menu.</summary>
		[JsonPropertyName("options")]
		public List<DiscordSelectOption>? Options { get; set; }

		/// <summary>Greyed-out prompt shown before anything is chosen or typed.</summary>
		[JsonPropertyName("placeholder")]
		public string? Placeholder { get; set; }

		/// <summary>Fewest choices a select menu will accept.</summary>
		[JsonPropertyName("min_values")]
		public int? MinValues { get; set; }

		/// <summary>Most choices a select menu will accept.</summary>
		[JsonPropertyName("max_values")]
		public int? MaxValues { get; set; }

		/// <summary>Whether a text input must be filled in before the modal can be submitted.</summary>
		[JsonPropertyName("required")]
		public bool? Required { get; set; }

		/// <summary>Pre-filled contents of a text input.</summary>
		[JsonPropertyName("value")]
		public string? Value { get; set; }

		/// <summary>Longest a text input will accept.</summary>
		[JsonPropertyName("max_length")]
		public int? MaxLength { get; set; }
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
		// Each entry is a component and whether it insists on a row to itself. Select menus do; buttons pack.
		readonly List<(DiscordComponent Component, bool NeedsOwnRow)> _items = new List<(DiscordComponent, bool)>();

		/// <summary>
		/// Whether anything has been added.
		/// </summary>
		public bool IsEmpty => _items.Count == 0;

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
			RequireUsableCustomId(customId);

			_items.Add((new DiscordComponent
			{
				Type = DiscordComponentType.Button,
				Style = style,
				Label = DiscordEmbedLimits.Truncate(label, DiscordComponentLimits.ButtonLabel),
				CustomId = customId,
				Disabled = disabled ? true : null,
			}, false));

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
			_items.Add((new DiscordComponent
			{
				Type = DiscordComponentType.Button,
				Style = DiscordButtonStyle.Link,
				Label = DiscordEmbedLimits.Truncate(label, DiscordComponentLimits.ButtonLabel),
				Url = url,
			}, false));

			return this;
		}

		/// <summary>
		/// Adds a dropdown.
		/// </summary>
		/// <remarks>
		/// This is the component that makes the hybrid Mark Fixed flow possible: a select menu is legal in a
		/// *message* and illegal in a modal, so the root-cause category is asked for afterwards rather than
		/// alongside. See <c>.claude/PLAN.md</c> section 3.3.4.
		///
		/// A select takes a whole action row, which is Discord's rule rather than a simplification here.
		/// </remarks>
		/// <param name="customId">Identifier echoed back with the chosen values.</param>
		/// <param name="options">Choices. Anything past the twenty-fifth is dropped.</param>
		/// <param name="placeholder">Prompt shown before anything is chosen.</param>
		/// <returns>This builder.</returns>
		/// <exception cref="ArgumentException">The custom id is empty or too long.</exception>
		public DiscordComponentBuilder AddSelect(string customId, IEnumerable<DiscordSelectOption> options, string? placeholder = null)
		{
			RequireUsableCustomId(customId);

			_items.Add((new DiscordComponent
			{
				Type = DiscordComponentType.StringSelect,
				CustomId = customId,
				Placeholder = placeholder == null
					? null
					: DiscordEmbedLimits.Truncate(placeholder, DiscordComponentLimits.SelectPlaceholder),
				Options = [.. options.Take(DiscordComponentLimits.SelectOptions)],
				MinValues = 1,
				MaxValues = 1,
			}, true));

			return this;
		}

		/// <summary>
		/// Lays the buttons out into rows.
		/// </summary>
		/// <returns>The action rows, or null if there is nothing to show.</returns>
		public List<DiscordComponent>? Build()
		{
			if (_items.Count == 0)
			{
				return null;
			}

			List<DiscordComponent> rows = new List<DiscordComponent>();
			bool lastRowIsExclusive = false;

			foreach ((DiscordComponent component, bool needsOwnRow) in _items)
			{
				bool needNewRow = rows.Count == 0
					|| needsOwnRow
					|| lastRowIsExclusive
					|| rows[^1].Components!.Count == DiscordComponentLimits.ButtonsPerRow;

				if (needNewRow)
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

					lastRowIsExclusive = needsOwnRow;
				}

				rows[^1].Components!.Add(component);
			}

			return rows;
		}

		/// <summary>
		/// Rejects a custom id that could not survive the round trip.
		/// </summary>
		/// <remarks>
		/// Deliberately not truncated, unlike every label here. A custom id is the only thing identifying what was
		/// used: a shortened one comes back as a verb nothing recognises, which is a silent no-op rather than a
		/// visible error.
		/// </remarks>
		static void RequireUsableCustomId(string customId)
		{
			if (String.IsNullOrEmpty(customId))
			{
				throw new ArgumentException("A component needs a custom id, or it cannot be acted on.", nameof(customId));
			}

			if (customId.Length > DiscordComponentLimits.CustomId)
			{
				throw new ArgumentException(
					$"Custom id '{customId}' is {customId.Length} characters; Discord allows {DiscordComponentLimits.CustomId}.",
					nameof(customId));
			}
		}
	}
}
