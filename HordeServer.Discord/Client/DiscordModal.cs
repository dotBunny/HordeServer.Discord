// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace HordeServer.Discord.Client
{
	/// <summary>
	/// A dialog Discord opens in front of whoever pressed a button.
	/// </summary>
	/// <remarks>
	/// **Text inputs only, five at most.** That single restriction is why Slack's Mark Fixed view cannot be ported
	/// across: it presents up to seven inputs, three of them radio groups and a select menu, none of which a Discord
	/// modal will accept. The agreed answer is a hybrid - the four text-typed fields here, and the root-cause
	/// category asked for afterwards as a select menu on an ephemeral message, where select menus are legal. See
	/// <c>.claude/PLAN.md</c> section 3.3.4.
	///
	/// The other rule worth knowing is about timing rather than shape, and it lives on
	/// <see cref="DiscordInteractionResponse.OpenModal"/>: a modal can only be the *immediate* answer to an
	/// interaction, never a deferred one.
	/// </remarks>
	public sealed class DiscordModal
	{
		/// <summary>
		/// Maximum characters in a modal title.
		/// </summary>
		public const int TitleLength = 45;

		/// <summary>Identifier echoed back when the modal is submitted.</summary>
		[JsonPropertyName("custom_id")]
		public string? CustomId { get; set; }

		/// <summary>Heading of the dialog.</summary>
		[JsonPropertyName("title")]
		public string? Title { get; set; }

		/// <summary>Action rows, each holding exactly one text input.</summary>
		[JsonPropertyName("components")]
		public List<DiscordComponent>? Components { get; set; }
	}

	/// <summary>
	/// Builds a modal Discord will accept.
	/// </summary>
	/// <remarks>
	/// Inputs past the fifth are **rejected rather than dropped**, which is the opposite of what the message builder
	/// does with surplus embeds. The reasoning differs: an embed that does not fit is content, and losing the least
	/// important one still leaves a useful notification, whereas a modal field that silently vanishes is a question
	/// the operator is never asked and whose absence they cannot see. If five is not enough, the flow needs
	/// redesigning - as Mark Fixed did.
	/// </remarks>
	public sealed class DiscordModalBuilder
	{
		readonly List<DiscordComponent> _inputs = new List<DiscordComponent>();
		readonly string _customId;
		readonly string _title;

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="customId">Identifier echoed back on submit. Routed exactly like a button's.</param>
		/// <param name="title">Heading, truncated to fit.</param>
		public DiscordModalBuilder(string customId, string title)
		{
			if (String.IsNullOrEmpty(customId))
			{
				throw new ArgumentException("A modal needs a custom id, or its submission cannot be routed.", nameof(customId));
			}

			if (customId.Length > DiscordComponentLimits.CustomId)
			{
				throw new ArgumentException(
					$"Custom id '{customId}' is {customId.Length} characters; Discord allows {DiscordComponentLimits.CustomId}.",
					nameof(customId));
			}

			_customId = customId;
			_title = DiscordEmbedLimits.Truncate(title, DiscordModal.TitleLength);
		}

		/// <summary>
		/// Adds a text field.
		/// </summary>
		/// <param name="customId">Identifier this field's value comes back under.</param>
		/// <param name="label">Prompt above the box, truncated to Discord's notably short 45 characters.</param>
		/// <param name="required">Whether the modal can be submitted without it.</param>
		/// <param name="value">Pre-filled contents.</param>
		/// <param name="placeholder">Greyed-out hint shown while empty.</param>
		/// <param name="paragraph">Whether to show a resizable box rather than a single line.</param>
		/// <returns>This builder.</returns>
		/// <exception cref="InvalidOperationException">The modal already has five fields.</exception>
		public DiscordModalBuilder AddTextInput(
			string customId,
			string label,
			bool required = false,
			string? value = null,
			string? placeholder = null,
			bool paragraph = false)
		{
			if (_inputs.Count == DiscordComponentLimits.TextInputsPerModal)
			{
				throw new InvalidOperationException(
					$"A Discord modal holds {DiscordComponentLimits.TextInputsPerModal} text inputs; '{customId}' would be "
					+ $"the {_inputs.Count + 1}th. Split the flow rather than dropping the field - see PLAN.md 3.3.4.");
			}

			_inputs.Add(new DiscordComponent
			{
				Type = DiscordComponentType.ActionRow,
				Components =
				[
					new DiscordComponent
					{
						Type = DiscordComponentType.TextInput,
						CustomId = customId,
						Label = DiscordEmbedLimits.Truncate(label, DiscordComponentLimits.TextInputLabel),
						Style = paragraph ? DiscordTextInputStyle.Paragraph : DiscordTextInputStyle.Short,
						Required = required,
						Value = value == null ? null : DiscordEmbedLimits.Truncate(value, DiscordComponentLimits.TextInputValue),
						Placeholder = placeholder,
						MaxLength = DiscordComponentLimits.TextInputValue,
					},
				],
			});

			return this;
		}

		/// <summary>
		/// Produces the modal.
		/// </summary>
		public DiscordModal Build()
			=> new DiscordModal
			{
				CustomId = _customId,
				Title = _title,
				Components = _inputs,
			};
	}
}
