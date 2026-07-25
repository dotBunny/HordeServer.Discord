// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;

namespace HordeServer.Discord.Notifications
{
	/// <summary>
	/// Parses the <c>;</c>-separated channel settings out of server configuration.
	/// </summary>
	/// <remarks>
	/// The separator matches Slack's <c>JobNotificationChannel</c>, because these settings will be filled in by
	/// someone reading their existing Slack configuration next to ours. That same person is very likely to paste a
	/// <c>#channel-name</c>, which Discord has no concept of - channels are snowflake ids and there is no lookup by
	/// name. Rejecting those loudly here turns a silent nothing-happens into one line in the log that says why.
	/// </remarks>
	public static class DiscordChannelList
	{
		/// <summary>
		/// Splits and validates a channel setting.
		/// </summary>
		/// <param name="setting">Raw configuration value, possibly null.</param>
		/// <param name="settingName">Name of the setting, for diagnostics.</param>
		/// <param name="logger">Logger to report unusable entries to.</param>
		/// <returns>The channel ids that were usable. Empty if the setting was unset or entirely invalid.</returns>
		public static IReadOnlyList<string> Parse(string? setting, string settingName, ILogger logger)
		{
			if (String.IsNullOrWhiteSpace(setting))
			{
				return Array.Empty<string>();
			}

			List<string> channels = new List<string>();

			foreach (string entry in setting.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				if (IsSnowflake(entry))
				{
					channels.Add(entry);
				}
				else if (entry.StartsWith('#'))
				{
					logger.LogError("{Setting} contains '{Entry}', which looks like a Slack channel name. Discord "
						+ "channels are numeric ids - copy one with Developer Mode enabled, via right-click on the "
						+ "channel then Copy Channel ID.", settingName, entry);
				}
				else
				{
					logger.LogError("{Setting} contains '{Entry}', which is not a Discord channel id.", settingName, entry);
				}
			}

			return channels;
		}

		static bool IsSnowflake(string value)
		{
			// Snowflakes are 64-bit ids rendered in decimal. Length is not checked beyond being plausible; they have
			// grown over the years and will keep doing so.
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
	}
}
