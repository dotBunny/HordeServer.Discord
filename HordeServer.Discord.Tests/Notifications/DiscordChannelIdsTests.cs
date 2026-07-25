// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using HordeServer.Discord.Client;
using HordeServer.Discord.Notifications;

namespace HordeServer.Discord.Tests.Notifications
{
	/// <summary>
	/// Tests that the two kinds of channel id stay distinguishable, and that borrowed text is escaped.
	/// </summary>
	/// <remarks>
	/// The whole routing design rests on Slack ids and Discord snowflakes being disjoint formats. If they ever
	/// overlap, a misplaced value stops being detectable and starts being silently wrong.
	/// </remarks>
	[TestClass]
	public sealed class DiscordChannelIdsTests
	{
		[TestMethod]
		[DataRow("C0832ESJUR5")]
		[DataRow("C085J3A6FHN")]
		[DataRow("GQWERTY1234")]
		[DataRow("D01ABCDEFGH")]
		public void RealSlackIdsAreRecognised(string value)
		{
			Assert.IsTrue(DiscordChannelIds.IsSlackChannelId(value));
			Assert.IsFalse(DiscordChannelIds.IsDiscordSnowflake(value), "The two formats must not overlap.");
		}

		[TestMethod]
		[DataRow("998877665544332211")]
		[DataRow("112233445566778899")]
		public void RealSnowflakesAreRecognised(string value)
		{
			Assert.IsTrue(DiscordChannelIds.IsDiscordSnowflake(value));
			Assert.IsFalse(DiscordChannelIds.IsSlackChannelId(value), "The two formats must not overlap.");
		}

		[TestMethod]
		[DataRow("#horde-builds")]
		[DataRow("horde-builds")]
		[DataRow("12345")]
		[DataRow("")]
		public void NeitherFormatMatchesJunk(string value)
		{
			Assert.IsFalse(DiscordChannelIds.IsDiscordSnowflake(value));
			Assert.IsFalse(DiscordChannelIds.IsSlackChannelId(value));
		}

		[TestMethod]
		public void ASlackIdInADiscordSettingIsCalledOutSpecifically()
		{
			string? problem = DiscordChannelIds.DescribeIfNotDiscordChannel("C0832ESJUR5");

			Assert.IsNotNull(problem);
			StringAssert.Contains(problem, "Slack channel id",
				"This is the realistic mistake - Horde's own settings hold Slack ids, so one gets pasted across. "
				+ "A generic 'not valid' message would send someone hunting in the wrong place.");
		}

		[TestMethod]
		public void AChannelNameGetsTheDeveloperModeHint()
		{
			string? problem = DiscordChannelIds.DescribeIfNotDiscordChannel("#horde-builds");

			Assert.IsNotNull(problem);
			StringAssert.Contains(problem, "Developer");
		}

		[TestMethod]
		public void AValidSnowflakeHasNoComplaint()
			=> Assert.IsNull(DiscordChannelIds.DescribeIfNotDiscordChannel("998877665544332211"));

		[TestMethod]
		[DataRow("C0832ESJUR5", DisplayName = "Slack channel id")]
		[DataRow("horde-builds", DisplayName = "bare name, as jobNotificationChannel holds")]
		public void BothFormsHordeActuallyUsesAreValidMapKeys(string key)
			=> Assert.IsNull(DiscordChannelIds.DescribeIfNotHordeChannel(key),
				"Horde carries a Slack id for most settings and a bare name for jobNotificationChannel and "
				+ "updateStreamsNotificationChannel. Both have to be usable as keys.");

		[TestMethod]
		public void ASnowflakeAsAMapKeyIsTheMappingTheWrongWayRound()
		{
			string? problem = DiscordChannelIds.DescribeIfNotHordeChannel("998877665544332211");

			Assert.IsNotNull(problem);
			StringAssert.Contains(problem, "Horde side");
		}

		[TestMethod]
		public void AHashPrefixedMapKeyNeverMatches()
		{
			string? problem = DiscordChannelIds.DescribeIfNotHordeChannel("#horde-builds");

			Assert.IsNotNull(problem, "Horde stores names without the '#', so a key carrying one is dead config.");
			StringAssert.Contains(problem, "leading '#'");
		}

		[TestMethod]
		public void SemicolonSeparatedSettingsAreSplitAndTrimmed()
		{
			CollectionAssert.AreEqual(
				new[] { "C0832ESJUR5", "C085J3A6FHN" },
				DiscordChannelIds.Split(" C0832ESJUR5 ; C085J3A6FHN ;; ").ToList());

			Assert.AreEqual(0, DiscordChannelIds.Split(null).Count);
			Assert.AreEqual(0, DiscordChannelIds.Split("  ").Count);
		}

		[TestMethod]
		public void MarkdownInBorrowedTextIsEscaped()
		{
			Assert.AreEqual(@"Build\_Step\_Name", DiscordMarkdown.Escape("Build_Step_Name"),
				"Underscores in step names would otherwise italicise half the message.");
			Assert.AreEqual(@"error C2039: '\_\_ptr32'", DiscordMarkdown.Escape("error C2039: '__ptr32'"));
			Assert.AreEqual(@"\[not a link\](url)", DiscordMarkdown.Escape("[not a link](url)"));
			Assert.AreEqual(@"\<@everyone\>", DiscordMarkdown.Escape("<@everyone>"));
			Assert.AreEqual("nothing to escape", DiscordMarkdown.Escape("nothing to escape"));
		}
	}
}
