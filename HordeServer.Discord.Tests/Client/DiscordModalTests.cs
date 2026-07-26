// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;
using HordeServer.Discord.Client;

namespace HordeServer.Discord.Tests.Client
{
	/// <summary>
	/// Tests for the modal half of the hybrid Mark Fixed flow.
	/// </summary>
	/// <remarks>
	/// The constraint driving all of it is that a Discord modal takes text inputs and nothing else, five at most,
	/// where Slack's equivalent view presents seven fields including radio groups and a select. See
	/// <c>.claude/PLAN.md</c> section 3.3.4.
	/// </remarks>
	[TestClass]
	public sealed class DiscordModalTests
	{
		#region Building

		[TestMethod]
		public void TheFourTextFieldsOfMarkFixedFit()
		{
			// The four text-typed fields of Slack's markfixed view, which is the whole reason five is enough.
			DiscordModal modal = new DiscordModalBuilder("issue_abc_markfixed", "Mark Fixed")
				.AddTextInput("fix_cl", "Fix CL", required: true)
				.AddTextInput("rootcause_summary", "Root cause summary", paragraph: true)
				.AddTextInput("rootcause_cl", "Root cause CL")
				.AddTextInput("rootcause_dupeid", "Duplicate issue id")
				.Build();

			Assert.AreEqual(4, modal.Components!.Count);
			Assert.IsTrue(modal.Components.All(x => x.Type == DiscordComponentType.ActionRow));
			Assert.IsTrue(modal.Components.All(x => x.Components!.Count == 1),
				"Discord puts exactly one text input in each row of a modal.");
		}

		[TestMethod]
		public void ASixthFieldIsRejectedRatherThanDropped()
		{
			DiscordModalBuilder builder = new DiscordModalBuilder("issue_abc_markfixed", "Mark Fixed");

			for (int index = 0; index < DiscordComponentLimits.TextInputsPerModal; index++)
			{
				builder.AddTextInput($"field{index}", $"Field {index}");
			}

			// Unlike a surplus embed, which is content. A modal field that silently vanished would be a question the
			// operator is never asked and whose absence they cannot see.
			InvalidOperationException ex = Assert.ThrowsExactly<InvalidOperationException>(
				() => builder.AddTextInput("field5", "One too many"));

			StringAssert.Contains(ex.Message, "3.3.4");
		}

		[TestMethod]
		public void ALongLabelIsCutToFortyFive()
		{
			DiscordModal modal = new DiscordModalBuilder("issue_abc_markfixed", "Mark Fixed")
				.AddTextInput("summary", new string('x', 200))
				.Build();

			Assert.AreEqual(DiscordComponentLimits.TextInputLabel, modal.Components![0].Components![0].Label!.Length,
				"A modal label allows 45 characters, not the 256 an embed field name does.");
		}

		[TestMethod]
		public void ALongTitleIsCutRatherThanRejected()
		{
			DiscordModal modal = new DiscordModalBuilder("issue_abc_markfixed", new string('x', 100)).Build();

			Assert.AreEqual(DiscordModal.TitleLength, modal.Title!.Length);
		}

		[TestMethod]
		public void APrefilledFieldCarriesItsValue()
		{
			DiscordModal modal = new DiscordModalBuilder("issue_abc_markfixed", "Mark Fixed")
				.AddTextInput("rootcause_summary", "Root cause summary", value: "Bad merge in 12345", paragraph: true)
				.Build();

			DiscordComponent input = modal.Components![0].Components![0];

			Assert.AreEqual("Bad merge in 12345", input.Value);
			Assert.AreEqual(DiscordTextInputStyle.Paragraph, input.Style);
		}

		[TestMethod]
		public void OnlyTheFixChangelistIsRequired()
		{
			DiscordModal modal = new DiscordModalBuilder("issue_abc_markfixed", "Mark Fixed")
				.AddTextInput("fix_cl", "Fix CL", required: true)
				.AddTextInput("rootcause_cl", "Root cause CL")
				.Build();

			Assert.AreEqual(true, modal.Components![0].Components![0].Required);
			Assert.AreEqual(false, modal.Components[1].Components![0].Required,
				"Everything except the fix changelist is optional in Slack's view too.");
		}

		#endregion

		#region Responses

		[TestMethod]
		public void OpeningAModalSerialisesAsCallbackTypeNine()
		{
			DiscordModal modal = new DiscordModalBuilder("issue_abc_markfixed", "Mark Fixed")
				.AddTextInput("fix_cl", "Fix CL", required: true)
				.Build();

			JsonElement json = Serialise(DiscordInteractionResponse.OpenModal(modal));

			Assert.AreEqual(DiscordInteractionCallbackType.Modal, json.GetProperty("type").GetInt32());
			Assert.AreEqual("issue_abc_markfixed", json.GetProperty("data").GetProperty("custom_id").GetString());
			Assert.AreEqual("Mark Fixed", json.GetProperty("data").GetProperty("title").GetString());

			JsonElement input = json.GetProperty("data").GetProperty("components")[0].GetProperty("components")[0];

			Assert.AreEqual(DiscordComponentType.TextInput, input.GetProperty("type").GetInt32());
			Assert.AreEqual("fix_cl", input.GetProperty("custom_id").GetString());
		}

		[TestMethod]
		public void AnEphemeralReplyCarriesTheFlag()
		{
			JsonElement json = Serialise(DiscordInteractionResponse.Ephemeral(
				new DiscordMessageBuilder().WithContent("pick a category").Build()));

			Assert.AreEqual(DiscordInteractionCallbackType.ChannelMessageWithSource, json.GetProperty("type").GetInt32());
			Assert.AreEqual(DiscordMessageFlags.Ephemeral, json.GetProperty("data").GetProperty("flags").GetInt32(),
				"The category follow-up is a question for one person mid-task; in a shared triage channel it would "
				+ "be noise, and anyone could answer it.");
		}

		[TestMethod]
		public void ADeferralCarriesNoPayload()
		{
			JsonElement json = Serialise(DiscordInteractionResponse.Acknowledge());

			Assert.AreEqual(DiscordInteractionCallbackType.DeferredUpdateMessage, json.GetProperty("type").GetInt32());
			Assert.IsFalse(json.TryGetProperty("data", out _));
		}

		#endregion

		#region Reading a submission

		[TestMethod]
		public void SubmittedValuesAreFlattenedOutOfTheirRows()
		{
			DiscordInteraction interaction = Submission("""
				[
				  {"type":1,"components":[{"type":4,"custom_id":"fix_cl","value":"12345"}]},
				  {"type":1,"components":[{"type":4,"custom_id":"rootcause_summary","value":"Bad merge"}]}
				]
				""");

			IReadOnlyDictionary<string, string> values = interaction.GetModalValues();

			Assert.AreEqual("12345", values["fix_cl"]);
			Assert.AreEqual("Bad merge", values["rootcause_summary"]);
		}

		[TestMethod]
		public void AnUnfilledOptionalFieldComesBackEmptyRatherThanAbsent()
		{
			DiscordInteraction interaction = Submission("""
				[{"type":1,"components":[{"type":4,"custom_id":"rootcause_summary","value":""}]}]
				""");

			IReadOnlyDictionary<string, string> values = interaction.GetModalValues();

			Assert.IsTrue(values.ContainsKey("rootcause_summary"));
			Assert.AreEqual(String.Empty, values["rootcause_summary"]);

			// The distinction the hybrid flow turns on: the category dropdown is only offered when a root-cause
			// summary was actually written, and "left blank" arrives as a present-but-empty field.
			Assert.IsTrue(String.IsNullOrWhiteSpace(values["rootcause_summary"]));
		}

		[TestMethod]
		public void AButtonPressHasNoModalValues()
			=> Assert.AreEqual(0, new DiscordInteraction
			{
				Type = DiscordInteractionType.MessageComponent,
				Data = new DiscordInteractionData { CustomId = "issue_abc_ack" },
			}.GetModalValues().Count);

		#endregion

		static DiscordInteraction Submission(string components)
			=> new DiscordInteraction
			{
				Id = "1",
				Token = "t",
				Type = DiscordInteractionType.ModalSubmit,
				Data = new DiscordInteractionData
				{
					CustomId = "issue_abc_markfixed",
					Components = JsonDocument.Parse(components).RootElement.Clone(),
				},
			};

		static JsonElement Serialise(DiscordInteractionResponse response)
			=> JsonDocument.Parse(JsonSerializer.Serialize(response, new JsonSerializerOptions
			{
				DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			})).RootElement.Clone();
	}
}
