// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using HordeServer.Issues;
using HordeServer.Streams;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HordeServer.Discord.Notifications
{
	/// <summary>
	/// Reports which of Horde's channels have no Discord mapping.
	/// </summary>
	/// <remarks>
	/// Both sides of the mapping are opaque ids, so a gap in it is invisible to anyone reading either config on its
	/// own - and the way you would otherwise discover one is a notification that never arrives, days later, for a
	/// workflow nobody was watching. Walking Horde's own configuration and naming every unmapped channel turns that
	/// into a few lines at startup.
	///
	/// Reported rather than enforced. A stream whose triage channel has no Discord counterpart is a perfectly
	/// reasonable state to be in while the map is being filled out, and Slack is still delivering it.
	/// </remarks>
	public sealed class DiscordRoutingReport : IHostedService, IDisposable
	{
		readonly DiscordChannelResolver _channels;
		readonly DiscordUserResolver _users;
		readonly IOptionsMonitor<BuildConfig> _buildConfig;
		readonly IOptions<BuildServerConfig> _buildServerConfig;
		readonly DiscordServerConfig _serverConfig;
		readonly ILogger _logger;

		IDisposable? _subscription;

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="channels">Resolver holding the channel map to check against.</param>
		/// <param name="users">Resolver holding the role map to check against.</param>
		/// <param name="buildConfig">Build plugin global configuration, which holds the stream and workflow routing.</param>
		/// <param name="buildServerConfig">Build plugin server configuration, which holds the base channels.</param>
		/// <param name="serverConfig">Discord server configuration.</param>
		/// <param name="logger">Logger to report gaps to.</param>
		public DiscordRoutingReport(DiscordChannelResolver channels, DiscordUserResolver users, IOptionsMonitor<BuildConfig> buildConfig, IOptions<BuildServerConfig> buildServerConfig, IOptions<DiscordServerConfig> serverConfig, ILogger<DiscordRoutingReport> logger)
		{
			_channels = channels;
			_users = users;
			_buildConfig = buildConfig;
			_buildServerConfig = buildServerConfig;
			_serverConfig = serverConfig.Value;
			_logger = logger;
		}

		/// <inheritdoc/>
		public Task StartAsync(CancellationToken cancellationToken)
		{
			if (_serverConfig.IsConfigured)
			{
				// Re-run on reload: the point of a hot-reloadable map is that somebody is editing it, and the
				// report is most useful immediately after they do.
				_subscription = _buildConfig.OnChange((_, _) => Report());
				Report();
			}

			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public Task StopAsync(CancellationToken cancellationToken)
		{
			Dispose();
			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public void Dispose()
		{
			_subscription?.Dispose();
			_subscription = null;
		}

		/// <summary>
		/// Collects every channel Horde might route to, and reports the ones with no Discord mapping.
		/// </summary>
		public void Report()
		{
			SortedDictionary<string, List<string>> unmapped = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
			SortedDictionary<string, List<string>> unmappedAliases = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
			int total = 0;
			int totalAliases = 0;

			void Record(SortedDictionary<string, List<string>> into, string key, string usedBy)
			{
				if (!into.TryGetValue(key, out List<string>? users))
				{
					into[key] = users = new List<string>();
				}

				if (users.Count < 5 && !users.Contains(usedBy))
				{
					users.Add(usedBy);
				}
			}

			void Check(string? hordeChannel, string usedBy)
			{
				foreach (string channel in DiscordChannelIds.Split(hordeChannel))
				{
					total++;

					if (!_channels.IsMapped(channel))
					{
						Record(unmapped, channel, usedBy);
					}
				}
			}

			void CheckAlias(string? alias, string usedBy)
			{
				if (String.IsNullOrWhiteSpace(alias))
				{
					return;
				}

				totalAliases++;

				if (!_users.IsRoleMapped(alias))
				{
					Record(unmappedAliases, alias, usedBy);
				}
			}

			BuildServerConfig buildServerConfig = _buildServerConfig.Value;
			Check(buildServerConfig.JobNotificationChannel, "server: jobNotificationChannel");
			Check(buildServerConfig.AgentNotificationChannel, "server: agentNotificationChannel");
			Check(buildServerConfig.ConfigNotificationChannel, "server: configNotificationChannel");
			Check(buildServerConfig.UpdateStreamsNotificationChannel, "server: updateStreamsNotificationChannel");
			Check(buildServerConfig.DeviceReportChannel, "server: deviceReportChannel");

			if (!TryGetStreams(out IReadOnlyList<StreamConfig>? streams))
			{
				// Global config has not loaded yet. The OnChange subscription will bring us back.
				return;
			}

			foreach (StreamConfig stream in streams)
			{
				Check(stream.TriageChannel, $"stream {stream.Id}");

				foreach (WorkflowConfig workflow in stream.Workflows)
				{
					Check(workflow.ReportChannel, $"stream {stream.Id}, workflow {workflow.Id}: reportChannel");
					Check(workflow.TriageChannel, $"stream {stream.Id}, workflow {workflow.Id}: triageChannel");

					CheckAlias(workflow.TriageAlias, $"stream {stream.Id}, workflow {workflow.Id}: triageAlias");
					CheckAlias(workflow.EscalateAlias, $"stream {stream.Id}, workflow {workflow.Id}: escalateAlias");

					foreach ((string issueType, string alias) in workflow.TriageTypeAliases ?? [])
					{
						CheckAlias(alias, $"stream {stream.Id}, workflow {workflow.Id}: triageTypeAliases[{issueType}]");
					}
				}

				foreach (TemplateRefConfig template in stream.Templates)
				{
					Check(template.TriageChannel, $"stream {stream.Id}, template {template.Id}");
				}
			}

			if (unmapped.Count == 0)
			{
				_logger.LogInformation("Discord channel routing: all {Total} Horde channel reference(s) are mapped.", total);
			}
			else
			{
				_logger.LogWarning("Discord channel routing: {Unmapped} of {Total} Horde channel reference(s) have no "
					+ "Discord mapping.", unmapped.Count, total);

				foreach ((string channel, List<string> usedBy) in unmapped)
				{
					_logger.LogWarning("  unmapped Horde channel '{Channel}' - used by {UsedBy}", channel, String.Join("; ", usedBy));
				}
			}

			// Reported at information rather than warning, and only when there is something to say. Aliases are not
			// used until issue triage lands, so a gap here is not yet costing anyone a notification - it is a heads-up
			// that it will.
			if (unmappedAliases.Count > 0)
			{
				_logger.LogInformation("Discord role routing: {Unmapped} of {Total} Horde alias reference(s) have no "
					+ "Discord role mapping. Issue triage will name them in plain text rather than pinging them.",
					unmappedAliases.Count, totalAliases);

				foreach ((string alias, List<string> usedBy) in unmappedAliases)
				{
					_logger.LogInformation("  unmapped Horde alias '{Alias}' - used by {UsedBy}", alias, String.Join("; ", usedBy));
				}
			}
		}

		bool TryGetStreams([NotNullWhen(true)] out IReadOnlyList<StreamConfig>? streams)
		{
			try
			{
				streams = _buildConfig.CurrentValue.Streams;
				return true;
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Build configuration is not available yet; deferring the Discord routing report.");
				streams = null;
				return false;
			}
		}
	}
}
