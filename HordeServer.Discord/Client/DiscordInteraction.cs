// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace HordeServer.Discord.Client
{
	/// <summary>
	/// Interaction types, as they appear in the <c>type</c> field.
	/// </summary>
	public static class DiscordInteractionType
	{
		/// <summary>A reachability check. Only ever seen by HTTP endpoints, never over the gateway.</summary>
		public const int Ping = 1;

		/// <summary>A slash command.</summary>
		public const int ApplicationCommand = 2;

		/// <summary>A button or select menu was used.</summary>
		public const int MessageComponent = 3;

		/// <summary>A modal was submitted.</summary>
		public const int ModalSubmit = 5;
	}

	/// <summary>
	/// The kinds of reply an interaction accepts.
	/// </summary>
	/// <remarks>
	/// Which one is used decides what the person who clicked sees while the work happens.
	/// <see cref="DeferredUpdateMessage"/> is the quiet one - the button stops spinning, nothing else changes, and
	/// the message can be edited whenever the work finishes. That is what triage wants, because the answer is a
	/// rewritten message rather than a reply.
	/// </remarks>
	public static class DiscordInteractionCallbackType
	{
		/// <summary>Reply to a ping.</summary>
		public const int Pong = 1;

		/// <summary>Reply with a new message.</summary>
		public const int ChannelMessageWithSource = 4;

		/// <summary>Show "thinking", then post a reply later.</summary>
		public const int DeferredChannelMessageWithSource = 5;

		/// <summary>Acknowledge silently, then edit the original message later.</summary>
		public const int DeferredUpdateMessage = 6;

		/// <summary>Replace the message the component is on, immediately.</summary>
		public const int UpdateMessage = 7;

		/// <summary>Open a modal.</summary>
		public const int Modal = 9;
	}

	/// <summary>
	/// An interaction, in the shape it arrives over the gateway.
	/// </summary>
	/// <remarks>
	/// A partial model. Discord sends a great deal more - the full guild member, resolved entities, the whole message
	/// the component sits on - and none of it is needed to act on a button whose identity is entirely in its custom
	/// id.
	/// </remarks>
	public sealed class DiscordInteraction
	{
		/// <summary>Snowflake of this interaction, half of what a response is addressed to.</summary>
		[JsonPropertyName("id")]
		public string? Id { get; set; }

		/// <summary>Application the interaction belongs to.</summary>
		[JsonPropertyName("application_id")]
		public string? ApplicationId { get; set; }

		/// <summary>See <see cref="DiscordInteractionType"/>.</summary>
		[JsonPropertyName("type")]
		public int Type { get; set; }

		/// <summary>
		/// Continuation token, the other half of the response address.
		/// </summary>
		/// <remarks>
		/// Valid for fifteen minutes, and it is what makes the deferred pattern work: the three-second deadline is on
		/// the *first* response only, and everything after that is addressed by this token rather than by the socket
		/// the interaction arrived on.
		/// </remarks>
		[JsonPropertyName("token")]
		public string? Token { get; set; }

		/// <summary>What was used, and how.</summary>
		[JsonPropertyName("data")]
		public DiscordInteractionData? Data { get; set; }

		/// <summary>Channel the component's message is in.</summary>
		[JsonPropertyName("channel_id")]
		public string? ChannelId { get; set; }

		/// <summary>Guild the interaction happened in. Absent in a DM.</summary>
		[JsonPropertyName("guild_id")]
		public string? GuildId { get; set; }

		/// <summary>Who used it, in a guild.</summary>
		[JsonPropertyName("member")]
		public DiscordInteractionMember? Member { get; set; }

		/// <summary>Who used it, in a DM.</summary>
		[JsonPropertyName("user")]
		public DiscordInteractionUser? User { get; set; }

		/// <summary>The message the component is attached to.</summary>
		[JsonPropertyName("message")]
		public DiscordInteractionMessage? Message { get; set; }

		/// <summary>
		/// Snowflake of whoever used the component, wherever Discord chose to put it.
		/// </summary>
		/// <remarks>
		/// A guild interaction carries <c>member.user</c> and a DM carries <c>user</c>, never both. Both paths matter
		/// here, since triage happens in a channel and the DM copy of the same notification carries its own buttons.
		/// </remarks>
		[JsonIgnore]
		public string? UserId => Member?.User?.Id ?? User?.Id;

		/// <summary>
		/// The custom id of whatever was used, or null.
		/// </summary>
		[JsonIgnore]
		public string? CustomId => Data?.CustomId;

		/// <summary>
		/// What was typed into a submitted modal, keyed by each field's custom id.
		/// </summary>
		/// <remarks>
		/// Discord returns the fields nested a row deep, in the same shape they were sent, rather than as the flat
		/// map every caller wants. Empty optional fields come back as empty strings rather than being omitted, so
		/// the distinction callers actually care about - "left blank" - is a
		/// <see cref="String.IsNullOrWhiteSpace"/> check, not a missing key.
		/// </remarks>
		/// <returns>Field values by custom id. Empty if this is not a modal submission.</returns>
		public IReadOnlyDictionary<string, string> GetModalValues()
		{
			Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);

			if (Data?.Components.ValueKind != JsonValueKind.Array)
			{
				return values;
			}

			foreach (JsonElement row in Data.Components.EnumerateArray())
			{
				if (!row.TryGetProperty("components", out JsonElement children) || children.ValueKind != JsonValueKind.Array)
				{
					continue;
				}

				foreach (JsonElement field in children.EnumerateArray())
				{
					if (field.TryGetProperty("custom_id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
					{
						values[id.GetString()!] = field.TryGetProperty("value", out JsonElement value) && value.ValueKind == JsonValueKind.String
							? value.GetString()!
							: String.Empty;
					}
				}
			}

			return values;
		}

		/// <summary>
		/// Where a response should edit, once the interaction has been acknowledged.
		/// </summary>
		[JsonIgnore]
		public DiscordMessageReference? MessageReference
			=> Message?.Id == null || (ChannelId ?? Message.ChannelId) == null
				? null
				: new DiscordMessageReference((ChannelId ?? Message.ChannelId)!, Message.Id);
	}

	/// <summary>
	/// What was used, and with what values.
	/// </summary>
	public sealed class DiscordInteractionData
	{
		/// <summary>Identifier of the component that was used, or the name of a submitted modal.</summary>
		[JsonPropertyName("custom_id")]
		public string? CustomId { get; set; }

		/// <summary>Which kind of component it was.</summary>
		[JsonPropertyName("component_type")]
		public int? ComponentType { get; set; }

		/// <summary>Selected values, for a select menu.</summary>
		[JsonPropertyName("values")]
		public List<string>? Values { get; set; }

		/// <summary>Submitted rows, for a modal.</summary>
		[JsonPropertyName("components")]
		public JsonElement Components { get; set; }
	}

	/// <summary>
	/// A guild member, cut down to the part that identifies them.
	/// </summary>
	public sealed class DiscordInteractionMember
	{
		/// <summary>The account behind the membership.</summary>
		[JsonPropertyName("user")]
		public DiscordInteractionUser? User { get; set; }
	}

	/// <summary>
	/// A user, cut down to the part that identifies them.
	/// </summary>
	public sealed class DiscordInteractionUser
	{
		/// <summary>User snowflake.</summary>
		[JsonPropertyName("id")]
		public string? Id { get; set; }

		/// <summary>Account name, for logging.</summary>
		[JsonPropertyName("username")]
		public string? Username { get; set; }
	}

	/// <summary>
	/// The message a component is attached to.
	/// </summary>
	public sealed class DiscordInteractionMessage
	{
		/// <summary>Message snowflake.</summary>
		[JsonPropertyName("id")]
		public string? Id { get; set; }

		/// <summary>Channel the message is in.</summary>
		[JsonPropertyName("channel_id")]
		public string? ChannelId { get; set; }
	}

	/// <summary>
	/// A reply to an interaction.
	/// </summary>
	public sealed class DiscordInteractionResponse
	{
		/// <summary>See <see cref="DiscordInteractionCallbackType"/>.</summary>
		[JsonPropertyName("type")]
		public int Type { get; set; }

		/// <summary>
		/// The payload, whose shape depends on <see cref="Type"/>.
		/// </summary>
		/// <remarks>
		/// A <see cref="DiscordMessage"/> for the callback types that send or replace one, a
		/// <see cref="DiscordModal"/> for <see cref="DiscordInteractionCallbackType.Modal"/>, and absent for the
		/// deferrals. Typed as <see cref="Object"/> because Discord reuses one field name for both and two
		/// properties cannot share it - <c>System.Text.Json</c> serialises an object-declared property by its
		/// runtime type, which produces exactly the right thing here.
		/// </remarks>
		[JsonPropertyName("data")]
		public object? Data { get; set; }

		/// <summary>
		/// Acknowledges a component press without changing anything yet.
		/// </summary>
		public static DiscordInteractionResponse Acknowledge()
			=> new DiscordInteractionResponse { Type = DiscordInteractionCallbackType.DeferredUpdateMessage };

		/// <summary>
		/// Replaces the message the component is on.
		/// </summary>
		/// <param name="message">Replacement content.</param>
		public static DiscordInteractionResponse Update(DiscordMessage message)
			=> new DiscordInteractionResponse { Type = DiscordInteractionCallbackType.UpdateMessage, Data = message };

		/// <summary>
		/// Opens a modal in front of whoever used the component.
		/// </summary>
		/// <remarks>
		/// **This can only ever be the first response to an interaction.** Once an interaction has been
		/// acknowledged - even with a deferral - Discord refuses to open a modal against it, because there is no
		/// longer anything for the dialog to attach to. That is squarely at odds with acknowledging everything up
		/// front to beat the three-second deadline, so a handler that opens a modal has to be registered as
		/// answering for itself. See <c>DiscordInteractionRouter.Register</c>.
		/// </remarks>
		/// <param name="modal">Dialog to open.</param>
		public static DiscordInteractionResponse OpenModal(DiscordModal modal)
			=> new DiscordInteractionResponse { Type = DiscordInteractionCallbackType.Modal, Data = modal };

		/// <summary>
		/// Replies with a message only the person who clicked can see.
		/// </summary>
		/// <remarks>
		/// How the root-cause category follow-up is asked for: it is a question for one person mid-task, and posting
		/// it into a shared triage channel would be noise for everyone else and would let anyone answer it.
		/// </remarks>
		/// <param name="message">Message to show. Its ephemeral flag is set here.</param>
		public static DiscordInteractionResponse Ephemeral(DiscordMessage message)
		{
			message.Flags = DiscordMessageFlags.Ephemeral;

			return new DiscordInteractionResponse
			{
				Type = DiscordInteractionCallbackType.ChannelMessageWithSource,
				Data = message,
			};
		}
	}

	/// <summary>
	/// The identity a button carries, and the only thing that comes back when it is pressed.
	/// </summary>
	/// <remarks>
	/// Slack's grammar, kept deliberately: <c>issue_{id}_{verb}</c>, or <c>issue_{id}_{verb}_{userId}</c> where the
	/// action is about somebody in particular. There is nothing wrong with it, Horde's own operators already read it
	/// in log lines, and inventing a second one would mean two things to recognise for no gain. Discord allows
	/// <see cref="DiscordComponentLimits.CustomId"/> characters, comfortably more than a 24-character hex id plus a
	/// verb needs.
	///
	/// The separator is unambiguous only because **no part may contain an underscore** - verbs are single words
	/// (<c>ack</c>, <c>accept</c>, <c>decline</c>, <c>markfixed</c>) and the ids either side are hex. A verb like
	/// <c>mark_fixed</c> would parse as a different shape entirely and silently do nothing, so if one is ever needed,
	/// this has to change first.
	/// </remarks>
	/// <param name="Scope">What kind of thing this acts on. <c>issue</c> is the only one so far.</param>
	/// <param name="Id">Identifier of the thing being acted on.</param>
	/// <param name="Verb">What to do to it.</param>
	/// <param name="UserId">Who it is about, when the action names somebody.</param>
	public sealed record DiscordCustomId(string Scope, string Id, string Verb, string? UserId = null)
	{
		/// <summary>
		/// Scope used by everything issue triage sends.
		/// </summary>
		public const string IssueScope = "issue";

		/// <summary>
		/// Renders the id for a button.
		/// </summary>
		public override string ToString()
			=> UserId == null ? $"{Scope}_{Id}_{Verb}" : $"{Scope}_{Id}_{Verb}_{UserId}";

		/// <summary>
		/// Reads a custom id back.
		/// </summary>
		/// <param name="value">Custom id as it arrived.</param>
		/// <param name="customId">The parsed id.</param>
		/// <returns>False if it is not one of ours, which is not an error - another bot's components can be on the
		/// same message.</returns>
		public static bool TryParse(string? value, out DiscordCustomId? customId)
		{
			customId = null;

			if (String.IsNullOrEmpty(value))
			{
				return false;
			}

			string[] parts = value.Split('_');

			if (parts.Length is < 3 or > 4 || parts.Any(String.IsNullOrEmpty))
			{
				return false;
			}

			customId = new DiscordCustomId(parts[0], parts[1], parts[2], parts.Length == 4 ? parts[3] : null);
			return true;
		}
	}
}
