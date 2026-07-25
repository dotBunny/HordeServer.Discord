// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using HordeServer.Discord.Client;

namespace HordeServer.Discord.Tests.Client
{
	/// <summary>
	/// Tests for the limits that span a whole message rather than one embed.
	/// </summary>
	[TestClass]
	public sealed class DiscordMessageBuilderTests
	{
		[TestMethod]
		public void MentionsAreInertUnlessAskedFor()
		{
			DiscordMessage message = new DiscordMessageBuilder().WithContent("@everyone the build broke").Build();

			Assert.IsNotNull(message.AllowedMentions,
				"Omitting allowed_mentions is not the same as sending an empty one - Discord's default is to honour "
				+ "every mention it can parse, including @everyone.");
			Assert.AreEqual(0, message.AllowedMentions.Parse.Count);
			Assert.IsNull(message.AllowedMentions.Users);
		}

		[TestMethod]
		public void NamedUsersCanBePinged()
		{
			DiscordMessage message = new DiscordMessageBuilder()
				.WithContent("<@123> broke it")
				.WithAllowedMentions(DiscordAllowedMentions.ForUsers(new[] { "123" }))
				.Build();

			CollectionAssert.AreEqual(new[] { "123" }, message.AllowedMentions!.Users);
			Assert.AreEqual(0, message.AllowedMentions.Parse.Count, "Naming users must not also re-enable @everyone.");
		}

		[TestMethod]
		public void EmbedsBeyondTheLimitAreDroppedAndReported()
		{
			DiscordMessageBuilder builder = new DiscordMessageBuilder();

			for (int idx = 0; idx < 14; idx++)
			{
				builder.AddEmbed(new DiscordEmbedBuilder().WithTitle($"Embed {idx}"));
			}

			DiscordMessage message = builder.Build();

			Assert.AreEqual(DiscordEmbedLimits.EmbedsPerMessage, message.Embeds!.Count);
			StringAssert.Contains(message.Content!, "4 further sections omitted");
		}

		[TestMethod]
		public void CombinedCharacterCeilingIsEnforcedAcrossEmbeds()
		{
			DiscordMessageBuilder builder = new DiscordMessageBuilder();

			// Three embeds of 2500 characters each are individually fine and collectively are not.
			for (int idx = 0; idx < 3; idx++)
			{
				builder.AddEmbed(new DiscordEmbedBuilder().WithDescription(new String('d', 2500)));
			}

			DiscordMessage message = builder.Build();
			List<DiscordEmbed> embeds = message.Embeds!;

			int total = embeds.Sum(x => x.CharacterCount);

			Assert.IsTrue(total <= DiscordEmbedLimits.CombinedEmbedCharacters, $"Sent {total} characters of embeds.");
			Assert.AreEqual(2, embeds.Count);
			StringAssert.Contains(message.Content!, "1 further section omitted", "One section, not one sections.");
		}

		[TestMethod]
		public void ExistingContentSurvivesTheOmissionNotice()
		{
			DiscordMessageBuilder builder = new DiscordMessageBuilder().WithContent("Build failed");

			for (int idx = 0; idx < 12; idx++)
			{
				builder.AddEmbed(new DiscordEmbedBuilder().WithTitle($"Embed {idx}"));
			}

			DiscordMessage message = builder.Build();

			StringAssert.StartsWith(message.Content!, "Build failed");
			StringAssert.Contains(message.Content!, "2 further sections omitted");
		}

		[TestMethod]
		public void OverLongContentIsTruncated()
		{
			DiscordMessage message = new DiscordMessageBuilder().WithContent(new String('c', 5000)).Build();

			Assert.AreEqual(DiscordEmbedLimits.MessageContent, message.Content!.Length);
			StringAssert.EndsWith(message.Content, DiscordEmbedLimits.TruncationMarker);
		}

		[TestMethod]
		public void MessageWithNoEmbedsOmitsTheArray()
		{
			DiscordMessage message = new DiscordMessageBuilder().WithContent("Just text").Build();

			Assert.IsNull(message.Embeds);
		}
	}
}
