// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Text.RegularExpressions;

namespace HordeServer.Discord.Tests
{
	/// <summary>
	/// Tests for the defaults an unconfigured server falls back on.
	/// </summary>
	[TestClass]
	public sealed class DiscordServerConfigTests
	{
		/// <summary>
		/// Matches a Slack-style <c>:shortcode:</c>, but not a custom Discord emoji, which is <c>&lt;:name:id&gt;</c>.
		/// </summary>
		static readonly Regex s_shortcode = new Regex(@"(?<!<):[a-z0-9_+-]+:");

		/// <summary>
		/// The emoji prefixes must be literal characters, not shortcodes.
		/// </summary>
		/// <remarks>
		/// Regression test. Both defaults were ported straight across from the Slack sink's settings and shipped as
		/// <c>:red_circle:</c> and <c>:warning:</c>, which Slack resolves and Discord does not - Discord's client
		/// expands a shortcode as a human types it, so anything a bot posts through the API keeps the punctuation.
		/// Every error and warning title in the plugin carries one of these, and the unit tests blank them both to
		/// keep the expected payloads readable, so nothing else here would notice it coming back.
		/// </remarks>
		[TestMethod]
		public void EmojiPrefixesAreLiteralNotShortcodes()
		{
			DiscordServerConfig config = new DiscordServerConfig();

			Assert.IsFalse(s_shortcode.IsMatch(config.ErrorPrefix),
				$"ErrorPrefix is '{config.ErrorPrefix}', which Discord will render as text rather than an emoji.");
			Assert.IsFalse(s_shortcode.IsMatch(config.WarningPrefix),
				$"WarningPrefix is '{config.WarningPrefix}', which Discord will render as text rather than an emoji.");
		}

		/// <summary>
		/// A prefix runs straight onto the title, so it has to bring its own separator.
		/// </summary>
		[TestMethod]
		public void EmojiPrefixesEndInASpace()
		{
			DiscordServerConfig config = new DiscordServerConfig();

			Assert.IsTrue(config.ErrorPrefix.EndsWith(' '), $"ErrorPrefix is '{config.ErrorPrefix}'.");
			Assert.IsTrue(config.WarningPrefix.EndsWith(' '), $"WarningPrefix is '{config.WarningPrefix}'.");
		}
	}
}
