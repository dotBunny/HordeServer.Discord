// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using HordeServer.Users;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HordeServer.Discord.Notifications
{
	/// <summary>
	/// Works out which Discord account belongs to a Horde user.
	/// </summary>
	/// <remarks>
	/// An interface because the answer will eventually come from more than one place. Today it is a hand-maintained
	/// map in configuration; a <c>/link</c> slash command letting people register themselves is the obvious next
	/// provider, and it would sit in front of the map rather than replacing it. Everything that needs a Discord id
	/// goes through here so that change is additive.
	/// </remarks>
	public interface IDiscordUserResolver
	{
		/// <summary>
		/// Finds the Discord account for a Horde user.
		/// </summary>
		/// <param name="user">Horde user to look up.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>The user's Discord snowflake, or null if nothing knows it.</returns>
		ValueTask<string?> GetUserIdAsync(IUser user, CancellationToken cancellationToken);

		/// <summary>
		/// Finds the Discord role standing in for one of Horde's user-group handles.
		/// </summary>
		/// <param name="alias">Handle as Horde carries it - a workflow's triage or escalation alias.</param>
		/// <returns>The role snowflake, or null if it is not mapped.</returns>
		string? GetRoleId(string? alias);
	}

	/// <summary>
	/// Resolves Discord accounts from the map in the hot-reloadable plugin configuration.
	/// </summary>
	/// <remarks>
	/// Discord has no equivalent of Slack's <c>users.lookupByEmail</c> - there is no way to find an account from an
	/// email address, and Horde knows nothing else about a person that Discord shares - so the association has to be
	/// supplied by hand. See <c>.claude/PLAN.md</c> section 3.3.1.
	///
	/// **Not cached, deliberately**, which departs from the plan's original sketch. Caching made sense while this was
	/// imagined as an API lookup like Slack's; over a dictionary in memory it would buy nothing and cost the hot
	/// reload that the map lives in the global config specifically to get. Adding somebody should start mentioning
	/// them, not start a cache expiry countdown. The DM *channel* id is cached, because that one is a round trip.
	/// </remarks>
	public sealed class DiscordUserResolver : IDiscordUserResolver
	{
		readonly IOptionsMonitor<DiscordConfig> _config;
		readonly ILogger _logger;

		// Warn-once per user. The people who go unmapped are the ones subscribed to a lot of steps, so this is the
		// difference between one actionable line and a log entry per notification for as long as the map is wrong.
		readonly ConcurrentDictionary<string, byte> _reported = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="config">Hot-reloadable plugin configuration holding the user and role maps.</param>
		/// <param name="logger">Logger for unmapped users.</param>
		public DiscordUserResolver(IOptionsMonitor<DiscordConfig> config, ILogger<DiscordUserResolver> logger)
		{
			_config = config;
			_logger = logger;
		}

		/// <inheritdoc/>
		public ValueTask<string?> GetUserIdAsync(IUser user, CancellationToken cancellationToken)
			=> new ValueTask<string?>(GetUserId(user));

		/// <summary>
		/// Finds the Discord account for a Horde user.
		/// </summary>
		/// <param name="user">Horde user to look up.</param>
		/// <returns>The user's Discord snowflake, or null if the map does not name them.</returns>
		public string? GetUserId(IUser user)
		{
			if (String.IsNullOrEmpty(user.Email))
			{
				Report(user, "their Horde account has no email address, which is the only thing the map can key on");
				return null;
			}

			if (_config.CurrentValue.ResolvedUsers.TryGetValue(user.Email, out string? userId))
			{
				return userId;
			}

			Report(user, $"'{user.Email}' is not in the userMap");
			return null;
		}

		/// <inheritdoc/>
		public string? GetRoleId(string? alias)
		{
			if (String.IsNullOrWhiteSpace(alias))
			{
				return null;
			}

			if (_config.CurrentValue.ResolvedRoles.TryGetValue(alias, out string? roleId))
			{
				return roleId;
			}

			if (_reported.TryAdd($"role:{alias}", 0))
			{
				_logger.LogWarning("No Discord role is mapped for Horde alias '{Alias}'; messages that would have "
					+ "pinged it will name it in plain text instead.", alias);
			}

			return null;
		}

		/// <summary>
		/// Whether a handle has a Discord role behind it.
		/// </summary>
		/// <remarks>Used by the routing report, which is about gaps in the map rather than about delivery.</remarks>
		/// <param name="alias">Handle as Horde carries it.</param>
		/// <returns>True if the map names it.</returns>
		public bool IsRoleMapped(string alias) => _config.CurrentValue.ResolvedRoles.ContainsKey(alias);

		void Report(IUser user, string why)
		{
			if (_reported.TryAdd(user.Id.ToString(), 0))
			{
				// Information rather than warning: an unmapped user is a perfectly reasonable state to be in while
				// the map is filled out, and their notifications still arrive - just addressed by name.
				_logger.LogInformation("No Discord account is mapped for Horde user '{Name}' ({UserId}) - {Why}. "
					+ "They will be named in plain text rather than mentioned, and cannot be sent direct messages.",
					user.Name, user.Id, why);
			}
		}
	}
}
