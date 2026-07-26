// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using EpicGames.Horde.Agents;
using EpicGames.Horde.Jobs;
using EpicGames.Horde.Jobs.Graphs;
using EpicGames.Horde.Users;
using HordeServer.Agents;
using HordeServer.Configuration;
using HordeServer.Devices;
using HordeServer.Issues;
using HordeServer.Jobs.TestData;
using HordeServer.Logs;
using HordeServer.Notifications;
using HordeServer.Streams;
using HordeServer.Users;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HordeServer.Discord.Notifications
{
	/// <summary>
	/// Sends Horde notifications to Discord.
	/// </summary>
	/// <remarks>
	/// Deliberately thin. Every member either forwards to <see cref="DiscordNotificationProcessor"/> or logs that
	/// its phase has not landed yet, so this file stays a readable list of the interface's members - which is what
	/// makes it tractable to diff against <see cref="INotificationSink"/> after an engine upgrade. Keep members in
	/// interface order for the same reason.
	///
	/// As of Phase 2 every broadcast member delivers - jobs, steps, configuration updates, agent and device reports
	/// and test health. Issues and the interactive triage that goes with them do not, and neither do the link
	/// members. See <c>.claude/PLAN.md</c> section 5 for the phase breakdown.
	/// </remarks>
	public sealed class DiscordNotificationSink : INotificationSink
	{
		readonly DiscordNotificationProcessor _processor;
		readonly DiscordServerConfig _serverConfig;
		readonly ILogger<DiscordNotificationSink> _logger;

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="processor">Turns notifications into Discord messages.</param>
		/// <param name="serverConfig">Server configuration for the Discord plugin.</param>
		/// <param name="logger">Logger for output.</param>
		public DiscordNotificationSink(DiscordNotificationProcessor processor, IOptions<DiscordServerConfig> serverConfig, ILogger<DiscordNotificationSink> logger)
		{
			_processor = processor;
			_serverConfig = serverConfig.Value;
			_logger = logger;

			// Logged at information on purpose: "the plugin loaded but is not going to send anything" is the single
			// most likely thing to be wrong, and it is invisible unless startup says so.
			if (!_serverConfig.IsConfigured)
			{
				_logger.LogInformation("Discord notification sink registered but no bot token is configured; notifications will be discarded");
			}
			else if (!_processor.CanSendJobNotifications)
			{
				_logger.LogWarning("Discord notification sink registered with a bot token but no usable JobNotificationChannel; job notifications will be discarded");
			}
			else
			{
				_logger.LogInformation("Discord notification sink registered (guild {GuildId}, interactions {Interactions})",
					_serverConfig.GuildId ?? "<unset>", _serverConfig.EnableInteractions ? "enabled" : "disabled");
			}
		}

		#region Jobs

		/// <inheritdoc/>
		public Task NotifyJobScheduledAsync(List<JobScheduledNotification> notifications, CancellationToken cancellationToken)
			=> _processor.NotifyJobScheduledAsync(notifications, cancellationToken);

		/// <inheritdoc/>
		public Task NotifyJobCompleteAsync(IJob job, IGraph graph, LabelOutcome outcome, CancellationToken cancellationToken)
			=> _processor.NotifyJobCompleteAsync(job, outcome, cancellationToken);

		/// <inheritdoc/>
		/// <remarks>Sent as a direct message, falling back to the job channel if the user cannot be reached.</remarks>
		public Task NotifyJobCompleteAsync(IUser user, IJob job, IGraph graph, LabelOutcome outcome, CancellationToken cancellationToken)
			=> _processor.NotifyJobCompleteToUserAsync(user, job, outcome, cancellationToken);

		/// <inheritdoc/>
		public Task NotifyJobStepAbortedAsync(IEnumerable<IUser>? usersToNotify, IJob job, IJobStepBatch batch, IJobStep step, INode node, List<ILogEventData> jobStepEventData, CancellationToken cancellationToken)
			=> _processor.NotifyJobStepAbortedAsync(job, step, node, jobStepEventData, usersToNotify, cancellationToken);

		/// <inheritdoc/>
		public Task NotifyJobStepCompleteAsync(IEnumerable<IUser>? usersToNotify, IJob job, IJobStepBatch batch, IJobStep step, INode node, List<ILogEventData> jobStepEventData, CancellationToken cancellationToken)
			=> _processor.NotifyJobStepCompleteAsync(job, step, node, jobStepEventData, usersToNotify, cancellationToken);

		/// <inheritdoc/>
		public Task NotifyLabelCompleteAsync(IUser user, IJob job, ILabel label, int labelIdx, LabelOutcome outcome, List<(string, JobStepOutcome, Uri)> stepData, CancellationToken cancellationToken)
			=> _processor.NotifyLabelCompleteAsync(job, label, outcome, stepData, user, cancellationToken);

		#endregion

		#region Issues

		/// <inheritdoc/>
		public Task NotifyIssueUpdatedAsync(IIssue issue, CancellationToken cancellationToken)
		{
			_logger.LogDebug("[Discord] IssueUpdated {IssueId}", issue.Id);
			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public Task SendIssueReportAsync(IssueReportGroup report, CancellationToken cancellationToken)
		{
			_logger.LogDebug("[Discord] IssueReport");
			return Task.CompletedTask;
		}

		#endregion

		#region Configuration

		/// <inheritdoc/>
		public Task NotifyConfigUpdateAsync(ConfigUpdateInfo info, CancellationToken cancellationToken)
			=> _processor.NotifyConfigUpdateAsync(info, cancellationToken);

		/// <inheritdoc/>
		public Task NotifyConfigUpdateFailureAsync(string errorMessage, string fileName, int? change = null, IUser? author = null, string? description = null, CancellationToken cancellationToken = default)
			=> _processor.NotifyConfigUpdateFailureAsync(errorMessage, fileName, change, author, description, cancellationToken);

		#endregion

		#region Farm operations

		/// <inheritdoc/>
		/// <remarks>Posted to the device channel naming the user until Phase 3 can DM them.</remarks>
		public Task NotifyDeviceServiceAsync(string message, IDevice? device = null, IDevicePool? pool = null, StreamConfig? streamConfig = null, IJob? job = null, IJobStep? step = null, INode? node = null, IUser? user = null, CancellationToken cancellationToken = default)
			=> _processor.NotifyDeviceServiceAsync(message, device, pool, streamConfig, job, step, node, user, cancellationToken);

		/// <inheritdoc/>
		public Task SendDeviceIssueReportAsync(DeviceIssueReport report, CancellationToken cancellationToken)
			=> _processor.SendDeviceIssueReportAsync(report, cancellationToken);

		/// <inheritdoc/>
		public Task SendAgentReportAsync(AgentReport report, CancellationToken cancellationToken)
			=> _processor.SendAgentReportAsync(report, cancellationToken);

		/// <inheritdoc/>
		public Task SendSessionConflictReportAsync(IReadOnlyList<(AgentId Id, int Count)> conflicts, CancellationToken cancellationToken)
			=> _processor.SendSessionConflictReportAsync(conflicts, cancellationToken);

		/// <inheritdoc/>
		public Task NotifyTestHealthReportAsync(ITestHealthReport report, string recipient, string[]? carbonCopies, CancellationToken cancellationToken)
			=> _processor.NotifyTestHealthReportAsync(report, recipient, carbonCopies, cancellationToken);

		#endregion

		#region Links

		/// <inheritdoc/>
		/// <remarks>
		/// Null unless this sink is the one answering for deep links - see
		/// <see cref="DiscordServerConfig.EnableDeepLinks"/>. Horde takes the first non-null answer from any sink.
		/// </remarks>
		public Task<string?> GetDirectMessageLinkAsync(IReadOnlyList<UserId> userIds, CancellationToken cancellationToken = default)
			=> _processor.GetDirectMessageLinkAsync(userIds, cancellationToken);

		/// <inheritdoc/>
		/// <remarks>See <see cref="GetDirectMessageLinkAsync"/>.</remarks>
		public Task<string?> GetChannelLinkAsync(string channel, CancellationToken cancellationToken = default)
			=> _processor.GetChannelLinkAsync(channel, cancellationToken);

		#endregion
	}
}
