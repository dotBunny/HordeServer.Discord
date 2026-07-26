// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using EpicGames.Horde.Jobs;
using HordeServer.Streams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HordeServer.Discord.Notifications
{
	/// <summary>
	/// The base notification channels Horde configures at server level.
	/// </summary>
	public enum DiscordChannelCategory
	{
		/// <summary>Job and step outcomes.</summary>
		Job,

		/// <summary>Agent reports.</summary>
		Agent,

		/// <summary>Configuration update failures.</summary>
		Config,

		/// <summary>Stream update failures.</summary>
		UpdateStreams,

		/// <summary>Device reports.</summary>
		Device,
	}

	/// <summary>
	/// Works out where a notification should be posted.
	/// </summary>
	/// <remarks>
	/// Horde has already done the routing. Workflow, stream and template configuration decide which channel a given
	/// issue or report belongs in, and the answer reaches the sink as a Slack channel id - either on the
	/// notification itself, or on the server config for the base categories. All this does is translate that last
	/// hop into a Discord guild and channel, using the map in <see cref="DiscordConfig"/>.
	///
	/// Two escape hatches, in precedence order. A Discord-native override in <see cref="DiscordServerConfig"/> wins
	/// outright, so a deployment that does not run Slack at all never has to invent Slack channel ids. Otherwise the
	/// Build plugin's own Slack setting is translated, which is what makes "configure it once in Horde" work.
	/// </remarks>
	public sealed class DiscordChannelResolver
	{
		readonly IOptionsMonitor<DiscordConfig> _config;
		readonly DiscordServerConfig _serverConfig;
		readonly BuildServerConfig _buildServerConfig;
		readonly ILogger _logger;

		// Warn-once, keyed by the unmapped channel. A stream that has gone red is the exact situation that produces
		// both the most notifications and the most attention on the log; one line per distinct channel, not per
		// message, is the difference between a useful warning and a wall of noise.
		readonly ConcurrentDictionary<string, byte> _reported = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="config">Hot-reloadable plugin configuration holding the channel map.</param>
		/// <param name="serverConfig">Discord server configuration, for Discord-native overrides.</param>
		/// <param name="buildServerConfig">Build plugin server configuration, for the base Slack channels.</param>
		/// <param name="logger">Logger for routing problems.</param>
		public DiscordChannelResolver(IOptionsMonitor<DiscordConfig> config, IOptions<DiscordServerConfig> serverConfig, IOptions<BuildServerConfig> buildServerConfig, ILogger<DiscordChannelResolver> logger)
		{
			_config = config;
			_serverConfig = serverConfig.Value;
			_buildServerConfig = buildServerConfig.Value;
			_logger = logger;
		}

		/// <summary>
		/// Translates a channel id that came from Horde.
		/// </summary>
		/// <param name="hordeChannel">Slack channel id, as carried by a workflow, report or server setting.</param>
		/// <returns>Where to post, or null if there is nowhere to send it.</returns>
		public DiscordDestination? Resolve(string? hordeChannel)
		{
			if (String.IsNullOrWhiteSpace(hordeChannel))
			{
				return null;
			}

			DiscordConfig config = _config.CurrentValue;

			if (config.ResolvedChannels.TryGetValue(hordeChannel, out DiscordDestination? destination))
			{
				return destination;
			}

			ReportUnmapped(hordeChannel, config.ResolvedFallback);

			// Stamp the source onto the fallback so the message that lands there can say what it was meant for.
			// The configured fallback is a single shared instance, hence the copy.
			return config.ResolvedFallback == null
				? null
				: config.ResolvedFallback with { SourceChannel = hordeChannel };
		}

		/// <summary>
		/// Translates several of Horde's channel ids at once, dropping duplicates.
		/// </summary>
		/// <remarks>
		/// Two Horde channels mapped to the same Discord channel would otherwise produce two identical messages;
		/// deduplicating on the destination rather than the source is what prevents that.
		/// </remarks>
		/// <param name="hordeChannels">Slack channel ids.</param>
		/// <returns>The distinct destinations.</returns>
		public IReadOnlyList<DiscordDestination> ResolveAll(IEnumerable<string> hordeChannels)
		{
			List<DiscordDestination> destinations = new List<DiscordDestination>();
			HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

			foreach (string hordeChannel in hordeChannels)
			{
				DiscordDestination? destination = Resolve(hordeChannel);

				if (destination != null && seen.Add(destination.ChannelId))
				{
					destinations.Add(destination);
				}
			}

			return destinations;
		}

		/// <summary>
		/// Works out where one of the base notification categories should go.
		/// </summary>
		/// <param name="category">Category to resolve.</param>
		/// <returns>The distinct destinations, which may be empty.</returns>
		public IReadOnlyList<DiscordDestination> ResolveCategory(DiscordChannelCategory category)
		{
			IReadOnlyList<string> overrides = DiscordChannelIds.Split(GetDiscordOverride(category));

			if (overrides.Count > 0)
			{
				return BuildOverrides(overrides, category);
			}

			return ResolveAll(DiscordChannelIds.Split(GetHordeSetting(category)));
		}

		/// <summary>
		/// Works out where a finished job should be reported, mirroring how Horde routes job completions.
		/// </summary>
		/// <remarks>
		/// Job completion is routed by the job itself and by its stream, each with an optional outcome filter, and
		/// *not* by the server-level job channel - that one is for scheduling notices. Both are consulted, so a job
		/// can legitimately be reported twice to different channels.
		///
		/// One deliberate departure: when neither is configured, this falls back to the Discord-native
		/// <see cref="DiscordServerConfig.JobNotificationChannel"/> override. Horde would send nothing, but a fresh
		/// install with only the Discord channel filled in should not be silent. The Build plugin's own setting is
		/// *not* used as that fallback, because Horde means something different by it.
		/// </remarks>
		/// <param name="job">Job that finished.</param>
		/// <param name="streamConfig">Configuration of the job's stream, if it could be found.</param>
		/// <param name="outcome">Outcome, matched against each channel's filter.</param>
		/// <returns>The distinct destinations.</returns>
		public IReadOnlyList<DiscordDestination> ResolveJobCompletion(IJob job, StreamConfig? streamConfig, LabelOutcome outcome)
		{
			List<string> channels = new List<string>();

			// Tracked separately from the channels themselves, because "nobody said where this goes" and "somebody
			// said this outcome is not worth sending" both leave the list empty and must not be treated alike.
			bool configured = !String.IsNullOrWhiteSpace(job.NotificationChannel)
				|| !String.IsNullOrWhiteSpace(streamConfig?.NotificationChannel);

			if (PassesFilter(job.NotificationChannelFilter, outcome, job.NotificationChannel))
			{
				channels.AddRange(DiscordChannelIds.Split(job.NotificationChannel));
			}

			if (streamConfig != null && PassesFilter(streamConfig.NotificationChannelFilter, outcome, streamConfig.NotificationChannel))
			{
				channels.AddRange(DiscordChannelIds.Split(streamConfig.NotificationChannel));
			}

			if (channels.Count > 0)
			{
				return ResolveAll(channels);
			}

			// An outcome filter that excluded this notification is a decision, not a gap. Falling through to the
			// override here would post the very outcomes somebody asked not to hear about.
			if (configured)
			{
				return Array.Empty<DiscordDestination>();
			}

			IReadOnlyList<string> overrides = DiscordChannelIds.Split(_serverConfig.JobNotificationChannel);
			return overrides.Count > 0 ? BuildOverrides(overrides, DiscordChannelCategory.Job) : Array.Empty<DiscordDestination>();
		}

		/// <summary>
		/// Whether an outcome passes a Horde notification channel filter.
		/// </summary>
		/// <remarks>
		/// The filter is a <c>|</c>-separated list of <see cref="LabelOutcome"/> names; unset means everything
		/// passes. An unrecognised name is reported once and then simply does not match, which is Horde's own
		/// behaviour - a typo silently narrows the filter rather than widening it.
		/// </remarks>
		/// <param name="filter">Filter to apply, possibly null.</param>
		/// <param name="outcome">Outcome to test.</param>
		/// <param name="context">Channel the filter belongs to, for diagnostics.</param>
		/// <returns>True if the notification should be sent.</returns>
		public bool PassesFilter(string? filter, LabelOutcome outcome, string? context)
		{
			if (String.IsNullOrWhiteSpace(filter))
			{
				return true;
			}

			bool matched = false;

			foreach (string option in filter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				if (Enum.TryParse(option, true, out LabelOutcome parsed))
				{
					matched |= parsed == outcome;
				}
				else if (_reported.TryAdd($"filter:{filter}", 0))
				{
					_logger.LogWarning("Invalid option '{Option}' in notification channel filter '{Filter}' for "
						+ "channel '{Channel}'; it will never match.", option, filter, context ?? "<unset>");
				}
			}

			return matched;
		}

		/// <summary>
		/// Whether a Horde channel id has an explicit mapping, ignoring any fallback.
		/// </summary>
		/// <remarks>Used by the routing report, which is about gaps in the map rather than about delivery.</remarks>
		/// <param name="hordeChannel">Slack channel id.</param>
		/// <returns>True if the map names it.</returns>
		public bool IsMapped(string hordeChannel)
			=> _config.CurrentValue.ResolvedChannels.ContainsKey(hordeChannel);

		IReadOnlyList<DiscordDestination> BuildOverrides(IReadOnlyList<string> channelIds, DiscordChannelCategory category)
		{
			string? defaultGuildId = _config.CurrentValue.ResolvedDefaultGuildId;
			List<DiscordDestination> destinations = new List<DiscordDestination>();

			foreach (string channelId in channelIds)
			{
				string? problem = DiscordChannelIds.DescribeIfNotDiscordChannel(channelId);

				if (problem != null)
				{
					if (_reported.TryAdd(channelId, 0))
					{
						_logger.LogError("Discord {Category} notification channel: {Problem}", category, problem);
					}

					continue;
				}

				destinations.Add(new DiscordDestination(channelId, defaultGuildId, category.ToString()));
			}

			return destinations;
		}

		string? GetDiscordOverride(DiscordChannelCategory category) => category switch
		{
			DiscordChannelCategory.Job => _serverConfig.JobNotificationChannel,
			DiscordChannelCategory.Agent => _serverConfig.AgentNotificationChannel,
			DiscordChannelCategory.Config => _serverConfig.ConfigNotificationChannel,
			DiscordChannelCategory.UpdateStreams => _serverConfig.UpdateStreamsNotificationChannel,
			DiscordChannelCategory.Device => _serverConfig.DeviceNotificationChannel,
			_ => null,
		};

		string? GetHordeSetting(DiscordChannelCategory category) => category switch
		{
			DiscordChannelCategory.Job => _buildServerConfig.JobNotificationChannel,
			DiscordChannelCategory.Agent => _buildServerConfig.AgentNotificationChannel,
			DiscordChannelCategory.Config => _buildServerConfig.ConfigNotificationChannel,
			DiscordChannelCategory.UpdateStreams => _buildServerConfig.UpdateStreamsNotificationChannel,
			DiscordChannelCategory.Device => _buildServerConfig.DeviceReportChannel,
			_ => null,
		};

		void ReportUnmapped(string hordeChannel, DiscordDestination? fallback)
		{
			if (!_reported.TryAdd(hordeChannel, 0))
			{
				return;
			}

			if (fallback == null)
			{
				_logger.LogWarning("No Discord channel is mapped for Horde channel '{HordeChannel}', and no "
					+ "fallbackChannel is configured, so those notifications are being dropped.", hordeChannel);
			}
			else
			{
				_logger.LogWarning("No Discord channel is mapped for Horde channel '{HordeChannel}'; sending to the "
					+ "fallback channel {Fallback} instead.", hordeChannel, fallback);
			}
		}
	}
}
