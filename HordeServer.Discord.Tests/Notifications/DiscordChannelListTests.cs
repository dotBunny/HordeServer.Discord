// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using HordeServer.Discord.Client;
using HordeServer.Discord.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace HordeServer.Discord.Tests.Notifications
{
	/// <summary>
	/// Tests for channel routing configuration and message text escaping.
	/// </summary>
	[TestClass]
	public sealed class DiscordChannelListTests
	{
		[TestMethod]
		public void UnsetSettingYieldsNoChannels()
		{
			CollectionAssert.AreEqual(Array.Empty<string>(), Parse(null));
			CollectionAssert.AreEqual(Array.Empty<string>(), Parse("   "));
		}

		[TestMethod]
		public void SemicolonSeparatedIdsAreAllAccepted()
		{
			CollectionAssert.AreEqual(
				new[] { "123456789012345678", "987654321098765432" },
				Parse("123456789012345678;987654321098765432"));
		}

		[TestMethod]
		public void SurroundingWhitespaceIsIgnored()
		{
			CollectionAssert.AreEqual(new[] { "123456789012345678" }, Parse(" 123456789012345678 ; "));
		}

		[TestMethod]
		public void SlackStyleChannelNamesAreRejected()
		{
			CollectionAssert.AreEqual(Array.Empty<string>(), Parse("#horde-builds"),
				"Discord has no lookup by channel name, so a name silently routes nowhere. This is the likeliest "
				+ "misconfiguration, because these settings get filled in from the Slack ones.");
		}

		[TestMethod]
		public void OneBadEntryDoesNotDiscardTheGoodOnes()
		{
			CollectionAssert.AreEqual(new[] { "123456789012345678" }, Parse("#builds;123456789012345678"));
		}

		[TestMethod]
		public void NonNumericIdsAreRejected()
		{
			CollectionAssert.AreEqual(Array.Empty<string>(), Parse("not-an-id;12345"),
				"Too short to be a snowflake, and snowflakes are decimal.");
		}

		[TestMethod]
		public void MarkdownInBorrowedTextIsEscaped()
		{
			Assert.AreEqual(
				@"Build\_Step\_Name",
				DiscordMarkdown.Escape("Build_Step_Name"),
				"Underscores in step names would otherwise italicise half the message.");

			Assert.AreEqual(@"error C2039: '\_\_ptr32'", DiscordMarkdown.Escape("error C2039: '__ptr32'"));
			Assert.AreEqual("nothing to escape", DiscordMarkdown.Escape("nothing to escape"));
		}

		[TestMethod]
		public void LinkAndMentionSyntaxIsEscaped()
		{
			Assert.AreEqual(@"\[not a link\](url)", DiscordMarkdown.Escape("[not a link](url)"),
				"Square brackets have to go; the parentheses are harmless once the brackets are inert.");
			Assert.AreEqual(@"\<@everyone\>", DiscordMarkdown.Escape("<@everyone>"));
		}

		static List<string> Parse(string? setting)
			=> [.. DiscordChannelList.Parse(setting, "JobNotificationChannel", NullLogger.Instance)];
	}
}
