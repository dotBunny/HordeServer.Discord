// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

namespace HordeServer.Discord.Notifications
{
	/// <summary>
	/// Tells the two kinds of channel identifier apart.
	/// </summary>
	/// <remarks>
	/// Horde mostly addresses channels by **Slack channel id** - <c>C0832ESJUR5</c> - and Discord always by
	/// snowflake, <c>998877665544332211</c>. Those two formats are disjoint, which is what makes the mapping in
	/// <see cref="DiscordConfig"/> safe to key on the Horde side and makes a misplaced value detectable rather than
	/// merely broken. Slack ids are also stable across channel renames, so a mapping keyed on one does not rot when
	/// somebody tidies up their workspace.
	///
	/// **Horde is not consistent about it, though.** Two of the Build plugin's server settings hold a bare channel
	/// *name* rather than an id - the Slack sink prepends the <c>#</c> itself when it sends:
	///
	/// <list type="bullet">
	/// <item><c>JobNotificationChannel</c></item>
	/// <item><c>UpdateStreamsNotificationChannel</c></item>
	/// </list>
	///
	/// So a mapping key is "whatever string Horde carries for that channel", usually an id and occasionally a name.
	/// Both are accepted; only a key that is obviously the *Discord* side of the mapping, or one carrying a leading
	/// <c>#</c> Horde never stores, is worth complaining about.
	/// </remarks>
	public static class DiscordChannelIds
	{
		/// <summary>
		/// Whether a value looks like a Discord channel snowflake.
		/// </summary>
		/// <remarks>
		/// Snowflakes are 64-bit ids in decimal. The length range is deliberately loose - they have grown over the
		/// years and will keep doing so - and only has to be tight enough to separate them from Slack's.
		/// </remarks>
		/// <param name="value">Value to test.</param>
		/// <returns>True if it could be a Discord snowflake.</returns>
		public static bool IsDiscordSnowflake(string value)
		{
			if (value.Length is < 15 or > 25)
			{
				return false;
			}

			foreach (char character in value)
			{
				if (!Char.IsAsciiDigit(character))
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>
		/// Whether a value looks like a Slack channel id.
		/// </summary>
		/// <param name="value">Value to test.</param>
		/// <returns>True if it could be a Slack channel id.</returns>
		public static bool IsSlackChannelId(string value)
		{
			// C is a public channel, G a private one, D a direct message. Everything after is uppercase base-36.
			if (value.Length is < 8 or > 21 || value[0] is not ('C' or 'G' or 'D'))
			{
				return false;
			}

			foreach (char character in value.AsSpan(1))
			{
				if (!Char.IsAsciiDigit(character) && !Char.IsAsciiLetterUpper(character))
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>
		/// Splits one of Horde's <c>;</c>-separated channel settings.
		/// </summary>
		/// <remarks>The separator matches Slack's, because these settings are filled in from the Slack ones.</remarks>
		/// <param name="setting">Raw configuration value, possibly null.</param>
		/// <returns>The individual entries, trimmed, with empties removed.</returns>
		public static IReadOnlyList<string> Split(string? setting)
			=> String.IsNullOrWhiteSpace(setting)
				? Array.Empty<string>()
				: setting.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		/// <summary>
		/// Explains what is wrong with a mapping key, which is meant to be the Horde side of the pair.
		/// </summary>
		/// <remarks>
		/// Permissive on purpose: a Slack id and a bare channel name are both legitimate, and the two are impossible
		/// to tell apart from a typo. Only the two unambiguous mistakes are worth a warning.
		/// </remarks>
		/// <param name="key">The mapping key.</param>
		/// <returns>A diagnostic, or null if the key is plausible.</returns>
		public static string? DescribeIfNotHordeChannel(string key)
		{
			if (IsDiscordSnowflake(key))
			{
				return $"'{key}' is a Discord snowflake. The key is the Horde side of the mapping - a Slack channel "
					+ "id, or a bare channel name for jobNotificationChannel and updateStreamsNotificationChannel - "
					+ "and the Discord channel goes in the value.";
			}

			if (key.StartsWith('#'))
			{
				return $"'{key}' has a leading '#'. Horde stores channel names without one, so nothing will match "
					+ "this key.";
			}

			return null;
		}

		/// <summary>
		/// Explains what is wrong with a value that was meant to be a Discord channel id.
		/// </summary>
		/// <param name="value">The offending value.</param>
		/// <returns>A diagnostic, or null if the value is fine.</returns>
		public static string? DescribeIfNotDiscordChannel(string value)
		{
			if (IsDiscordSnowflake(value))
			{
				return null;
			}

			if (IsSlackChannelId(value))
			{
				return $"'{value}' is a Slack channel id. Map it to a Discord channel in the Discord plugin's "
					+ "'channels' configuration rather than using it directly.";
			}

			if (value.StartsWith('#'))
			{
				return $"'{value}' is a channel name. Discord addresses channels by numeric id - enable Developer "
					+ "Mode, then right-click the channel and choose Copy Channel ID.";
			}

			return $"'{value}' is not a Discord channel id.";
		}
	}
}
