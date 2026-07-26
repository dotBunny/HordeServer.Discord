// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Reflection;

namespace DiscordSmoke
{
	/// <summary>
	/// Where the smoke tool posts, and what it authenticates with.
	/// </summary>
	/// <remarks>
	/// Resolved the same way the build resolves <c>HordeBinDir</c>: a build-time value baked in from
	/// <c>Horde.local.props</c>, falling back to an environment variable. Nothing is hardcoded and nothing is
	/// committed.
	///
	/// <see cref="BotToken"/> is never printed, logged or included in <see cref="Describe"/>. Every diagnostic in
	/// this tool goes through <see cref="Describe"/> for exactly that reason.
	/// </remarks>
	sealed class SmokeSettings
	{
		public required string BotToken { get; init; }

		public required string GuildId { get; init; }

		public required string ChannelId { get; init; }

		public string? UserId { get; init; }

		public required Uri DashboardUrl { get; init; }

		/// <summary>
		/// Whether direct message and mention scenarios can run.
		/// </summary>
		public bool CanReachAUser => !String.IsNullOrEmpty(UserId);

		/// <summary>
		/// A summary safe to print. The token is deliberately absent rather than masked.
		/// </summary>
		public string Describe()
			=> $"""
				guild     {GuildId}
				channel   {ChannelId}
				user      {UserId ?? "<unset - direct message scenarios will be skipped>"}
				dashboard {DashboardUrl}
				""";

		/// <summary>
		/// Reads the settings, or explains what is missing.
		/// </summary>
		public static bool TryResolve(out SmokeSettings? settings, out string? problem)
		{
			string? botToken = Read("DiscordBotToken");
			string? guildId = Read("DiscordGuildId");
			string? channelId = Read("DiscordTestChannelId");

			List<string> missing = new List<string>();

			if (String.IsNullOrWhiteSpace(botToken))
			{
				missing.Add("DiscordBotToken");
			}

			if (String.IsNullOrWhiteSpace(guildId))
			{
				missing.Add("DiscordGuildId");
			}

			if (String.IsNullOrWhiteSpace(channelId))
			{
				missing.Add("DiscordTestChannelId");
			}

			if (missing.Count > 0)
			{
				settings = null;
				problem = $"""
					Not configured: {String.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} unset.

					Set them in Horde.local.props at the repo root - see Horde.local.props.template for the block to
					copy, and note that the file is git-ignored, which is what makes it a safe place for a token.
					Rebuild afterwards, because the values are baked in at build time.

					Alternatively export DISCORD_BOT_TOKEN, DISCORD_GUILD_ID and DISCORD_TEST_CHANNEL_ID.
					""";
				return false;
			}

			string dashboardUrl = Read("DiscordDashboardUrl") ?? "https://horde.example.com/";

			if (!Uri.TryCreate(dashboardUrl, UriKind.Absolute, out Uri? dashboard))
			{
				settings = null;
				problem = $"DiscordDashboardUrl is '{dashboardUrl}', which is not an absolute URL.";
				return false;
			}

			settings = new SmokeSettings
			{
				BotToken = botToken!,
				GuildId = guildId!,
				ChannelId = channelId!,
				UserId = Read("DiscordTestUserId"),
				DashboardUrl = dashboard,
			};

			problem = null;
			return true;
		}

		/// <summary>
		/// Reads a setting from the environment, then from what the build baked in.
		/// </summary>
		/// <remarks>
		/// Environment first, unlike the <c>HordeBinDir</c> lookup. That one resolves a path the build itself
		/// depends on; this resolves a credential, and being able to override one for a single run without editing a
		/// file and rebuilding is the more useful way round.
		/// </remarks>
		static string? Read(string name)
		{
			string environmentName = String.Concat(name.Select((c, i) => Char.IsUpper(c) && i > 0 ? "_" + c : c.ToString())).ToUpperInvariant();
			string? value = Environment.GetEnvironmentVariable(environmentName);

			if (!String.IsNullOrWhiteSpace(value))
			{
				return value.Trim();
			}

			value = Assembly.GetExecutingAssembly()
				.GetCustomAttributes<AssemblyMetadataAttribute>()
				.FirstOrDefault(x => x.Key == name)?.Value;

			return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
		}
	}
}
