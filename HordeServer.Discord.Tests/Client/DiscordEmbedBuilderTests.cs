// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using HordeServer.Discord.Client;

namespace HordeServer.Discord.Tests.Client
{
	/// <summary>
	/// Tests that the embed builder produces something Discord will accept, whatever it is handed.
	/// </summary>
	/// <remarks>
	/// Horde's input is unbounded - log excerpts, error lists, step names from a stream nobody has pruned. These
	/// limits are hard 400s, so every one of them failing means a notification that never arrives.
	/// </remarks>
	[TestClass]
	public sealed class DiscordEmbedBuilderTests
	{
		[TestMethod]
		public void ShortValuesArePassedThroughUnchanged()
		{
			DiscordEmbed embed = new DiscordEmbedBuilder()
				.WithTitle("Job complete")
				.WithDescription("Everything worked.")
				.AddField("Stream", "dethol-main", true)
				.Build();

			Assert.AreEqual("Job complete", embed.Title);
			Assert.AreEqual("Everything worked.", embed.Description);
			Assert.AreEqual(1, embed.Fields!.Count);
			Assert.AreEqual("dethol-main", embed.Fields[0].Value);
			Assert.IsTrue(embed.Fields[0].Inline);
		}

		[TestMethod]
		public void OverLongTitleIsTruncatedAndMarked()
		{
			DiscordEmbed embed = new DiscordEmbedBuilder().WithTitle(new String('x', 500)).Build();

			Assert.AreEqual(DiscordEmbedLimits.Title, embed.Title!.Length);
			StringAssert.EndsWith(embed.Title, DiscordEmbedLimits.TruncationMarker,
				"Truncation has to be visible - a silently shortened value reads as the whole story.");
		}

		[TestMethod]
		public void OverLongFieldValueIsTruncated()
		{
			DiscordEmbed embed = new DiscordEmbedBuilder().AddField("Errors", new String('e', 4000)).Build();

			Assert.AreEqual(DiscordEmbedLimits.FieldValue, embed.Fields![0].Value.Length);
			StringAssert.EndsWith(embed.Fields[0].Value, DiscordEmbedLimits.TruncationMarker);
		}

		[TestMethod]
		public void TruncationDoesNotSplitASurrogatePair()
		{
			// A limit of 22 puts the cut at index 21 - between the two halves of the emoji at 20 and 21.
			string value = new String('a', 20) + "\U0001F525" + new String('b', 10);
			string truncated = DiscordEmbedLimits.Truncate(value, 22);

			Assert.IsFalse(Char.IsHighSurrogate(truncated[^2]),
				"Cutting between surrogates produces an invalid string and Discord rejects the payload outright.");
			Assert.AreEqual(21, truncated.Length, "Backing off a surrogate costs one character; that is the trade.");
			StringAssert.EndsWith(truncated, DiscordEmbedLimits.TruncationMarker);
		}

		[TestMethod]
		public void FieldsBeyondTheLimitAreSummarised()
		{
			DiscordEmbedBuilder builder = new DiscordEmbedBuilder();

			for (int idx = 0; idx < 40; idx++)
			{
				builder.AddField($"Step {idx}", "Failed");
			}

			DiscordEmbed embed = builder.Build();

			Assert.AreEqual(DiscordEmbedLimits.FieldsPerEmbed, embed.Fields!.Count);
			Assert.AreEqual(DiscordEmbedLimits.TruncationMarker, embed.Fields[^1].Name);
			Assert.AreEqual("and 16 more", embed.Fields[^1].Value,
				"Twenty-four real fields are shown and the last slot reports the rest, so 40 - 24 = 16.");
		}

		[TestMethod]
		public void CombinedCeilingIsRespectedEvenWhenEveryValueIsIndividuallyLegal()
		{
			DiscordEmbedBuilder builder = new DiscordEmbedBuilder()
				.WithTitle(new String('t', DiscordEmbedLimits.Title))
				.WithDescription(new String('d', DiscordEmbedLimits.Description))
				.WithFooter(new String('f', DiscordEmbedLimits.FooterText));

			for (int idx = 0; idx < DiscordEmbedLimits.FieldsPerEmbed; idx++)
			{
				builder.AddField(new String('n', DiscordEmbedLimits.FieldName), new String('v', DiscordEmbedLimits.FieldValue));
			}

			DiscordEmbed embed = builder.Build();

			Assert.IsTrue(embed.CharacterCount <= DiscordEmbedLimits.CombinedEmbedCharacters,
				$"Every value was individually legal but they summed to {embed.CharacterCount}, over the "
				+ $"{DiscordEmbedLimits.CombinedEmbedCharacters} ceiling. The per-value limits do not imply the "
				+ "combined one.");
			Assert.AreEqual(DiscordEmbedLimits.TruncationMarker, embed.Fields![^1].Name,
				"Fields were dropped to fit, so the reader has to be told.");
		}

		[TestMethod]
		public void AnEnormousDescriptionAloneIsTrimmedToTheCombinedCeiling()
		{
			DiscordEmbed embed = new DiscordEmbedBuilder()
				.WithTitle(new String('t', DiscordEmbedLimits.Title))
				.WithDescription(new String('d', DiscordEmbedLimits.Description))
				.WithFooter(new String('f', DiscordEmbedLimits.FooterText))
				.Build();

			Assert.IsTrue(embed.CharacterCount <= DiscordEmbedLimits.CombinedEmbedCharacters);
			StringAssert.EndsWith(embed.Description, DiscordEmbedLimits.TruncationMarker);
		}

		[TestMethod]
		public void ExactlyTwentyFiveFieldsAreAllKept()
		{
			DiscordEmbedBuilder builder = new DiscordEmbedBuilder();

			for (int idx = 0; idx < DiscordEmbedLimits.FieldsPerEmbed; idx++)
			{
				builder.AddField($"Step {idx}", "Failed");
			}

			DiscordEmbed embed = builder.Build();

			Assert.AreEqual(DiscordEmbedLimits.FieldsPerEmbed, embed.Fields!.Count);
			Assert.AreNotEqual(DiscordEmbedLimits.TruncationMarker, embed.Fields[^1].Name,
				"Nothing was dropped, so no slot should have been given up to say otherwise.");
		}

		[TestMethod]
		public void EmbedWithNoFieldsOmitsTheArray()
		{
			DiscordEmbed embed = new DiscordEmbedBuilder().WithTitle("Nothing to add").Build();

			Assert.IsNull(embed.Fields, "An empty array and no array are different payloads; send neither.");
		}
	}
}
