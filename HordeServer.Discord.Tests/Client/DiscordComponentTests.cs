// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Text.Json;
using HordeServer.Discord.Client;

namespace HordeServer.Discord.Tests.Client
{
	/// <summary>
	/// Tests for the buttons a triage message carries, and the identity they carry back.
	/// </summary>
	[TestClass]
	public sealed class DiscordComponentTests
	{
		#region Layout

		[TestMethod]
		public void NoButtonsMeansNoComponents()
			=> Assert.IsNull(new DiscordComponentBuilder().Build(),
				"An empty components array is meaningful on an edit - it removes buttons - so it must not be sent "
				+ "by a message that simply has none.");

		[TestMethod]
		public void ButtonsFillARowBeforeStartingAnother()
		{
			DiscordComponentBuilder builder = new DiscordComponentBuilder();

			for (int index = 0; index < 6; index++)
			{
				builder.AddButton($"issue_1_verb{index}", $"Button {index}");
			}

			List<DiscordComponent> rows = builder.Build()!;

			Assert.AreEqual(2, rows.Count);
			Assert.AreEqual(DiscordComponentLimits.ButtonsPerRow, rows[0].Components!.Count);
			Assert.AreEqual(1, rows[1].Components!.Count);
		}

		[TestMethod]
		public void ButtonsPastTheLastRowAreDropped()
		{
			DiscordComponentBuilder builder = new DiscordComponentBuilder();

			for (int index = 0; index < 40; index++)
			{
				builder.AddButton($"issue_1_v{index}", $"Button {index}");
			}

			List<DiscordComponent> rows = builder.Build()!;

			Assert.AreEqual(DiscordComponentLimits.RowsPerMessage, rows.Count,
				"Discord rejects the whole message for one row too many, so the overflow has to go.");
			Assert.IsTrue(rows.All(x => x.Components!.Count <= DiscordComponentLimits.ButtonsPerRow));
		}

		[TestMethod]
		public void ALongLabelIsTruncatedRatherThanRejected()
		{
			List<DiscordComponent> rows = new DiscordComponentBuilder()
				.AddButton("issue_1_ack", new string('x', 500))
				.Build()!;

			Assert.AreEqual(DiscordComponentLimits.ButtonLabel, rows[0].Components![0].Label!.Length);
		}

		[TestMethod]
		public void AnOverlongCustomIdIsRejected()
		{
			// The one thing here that is not truncated. A shortened custom id comes back as a verb nothing
			// recognises, which is a silent no-op rather than a visible error.
			ArgumentException ex = Assert.ThrowsExactly<ArgumentException>(
				() => new DiscordComponentBuilder().AddButton(new string('x', 101), "Press"));

			StringAssert.Contains(ex.Message, "101");
		}

		[TestMethod]
		public void ALinkButtonCarriesNoCustomId()
		{
			List<DiscordComponent> rows = new DiscordComponentBuilder()
				.AddLink("https://horde.example.com/issue/1", "Open in Horde")
				.Build()!;

			DiscordComponent button = rows[0].Components![0];

			Assert.AreEqual(DiscordButtonStyle.Link, button.Style);
			Assert.IsNull(button.CustomId, "Discord rejects a link button that also has a custom id.");
			Assert.AreEqual("https://horde.example.com/issue/1", button.Url);
		}

		#endregion

		#region Serialisation

		[TestMethod]
		public void AMessageWithNoButtonsOmitsComponentsEntirely()
		{
			DiscordMessage message = new DiscordMessageBuilder()
				.WithContent("no buttons")
				.WithComponents(new DiscordComponentBuilder())
				.Build();

			StringAssert.DoesNotMatch(Serialise(message), new System.Text.RegularExpressions.Regex("components"),
				"Sending an empty array on an edit would strip the buttons off the message being edited.");
		}

		[TestMethod]
		public void RemovingButtonsSendsAnEmptyArray()
		{
			DiscordMessage message = new DiscordMessageBuilder()
				.WithContent("resolved")
				.WithoutComponents()
				.Build();

			JsonElement json = JsonDocument.Parse(Serialise(message)).RootElement;

			Assert.AreEqual(JsonValueKind.Array, json.GetProperty("components").ValueKind);
			Assert.AreEqual(0, json.GetProperty("components").GetArrayLength(),
				"An omitted components property leaves the existing buttons in place, so this is the only way to "
				+ "stop a resolved issue offering to resolve it again.");
		}

		[TestMethod]
		public void AButtonSerialisesTheWayDiscordExpects()
		{
			DiscordMessage message = new DiscordMessageBuilder()
				.WithComponents(new DiscordComponentBuilder().AddButton("issue_abc_ack", "Acknowledge", DiscordButtonStyle.Success))
				.Build();

			JsonElement row = JsonDocument.Parse(Serialise(message)).RootElement.GetProperty("components")[0];

			Assert.AreEqual(DiscordComponentType.ActionRow, row.GetProperty("type").GetInt32());

			JsonElement button = row.GetProperty("components")[0];

			Assert.AreEqual(DiscordComponentType.Button, button.GetProperty("type").GetInt32());
			Assert.AreEqual(DiscordButtonStyle.Success, button.GetProperty("style").GetInt32());
			Assert.AreEqual("issue_abc_ack", button.GetProperty("custom_id").GetString());
			Assert.AreEqual("Acknowledge", button.GetProperty("label").GetString());
		}

		#endregion

		#region Custom ids

		[TestMethod]
		public void ACustomIdRoundTrips()
		{
			DiscordCustomId original = new DiscordCustomId(DiscordCustomId.IssueScope, "65f0abc", "accept");

			Assert.IsTrue(DiscordCustomId.TryParse(original.ToString(), out DiscordCustomId? parsed));
			Assert.AreEqual(original, parsed);
		}

		[TestMethod]
		public void ACustomIdNamingSomebodyRoundTrips()
		{
			DiscordCustomId original = new DiscordCustomId(DiscordCustomId.IssueScope, "65f0abc", "decline", "200000000000000001");

			Assert.IsTrue(DiscordCustomId.TryParse(original.ToString(), out DiscordCustomId? parsed));
			Assert.AreEqual(original, parsed);
			Assert.AreEqual("200000000000000001", parsed!.UserId);
		}

		[TestMethod]
		public void TheGrammarMatchesSlacks()
			=> Assert.AreEqual("issue_65f0abc_ack",
				new DiscordCustomId(DiscordCustomId.IssueScope, "65f0abc", "ack").ToString(),
				"Kept deliberately - operators already read this shape in Horde's own logs.");

		[TestMethod]
		[DataRow("", "empty")]
		[DataRow("issue", "no id or verb")]
		[DataRow("issue_1", "no verb")]
		[DataRow("issue_1_ack_user_extra", "too many parts")]
		[DataRow("issue__ack", "an empty segment")]
		[DataRow("some-other-bots-button", "not ours at all")]
		public void SomethingElsesComponentIsNotOurs(string value, string why)
			=> Assert.IsFalse(DiscordCustomId.TryParse(value, out _), why);

		[TestMethod]
		public void AVerbContainingASeparatorWouldBeMisread()
		{
			// Not a bug so much as the constraint the grammar imposes, asserted so that adding such a verb fails here
			// rather than silently doing nothing in production.
			Assert.IsTrue(DiscordCustomId.TryParse("issue_1_mark_fixed", out DiscordCustomId? parsed));
			Assert.AreEqual("mark", parsed!.Verb);
			Assert.AreEqual("fixed", parsed.UserId,
				"An underscore in a verb parses as the user-id segment. Verbs must stay single words.");
		}

		#endregion

		static string Serialise(DiscordMessage message)
			=> JsonSerializer.Serialize(message, new JsonSerializerOptions
			{
				DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
			});
	}
}
