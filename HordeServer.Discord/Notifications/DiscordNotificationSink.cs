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
	/// Phase 0 skeleton: every member is a no-op that records the callback, which is enough to prove the plugin is
	/// discovered, its configuration binds, and the notification service is routing events to it. Real delivery
	/// arrives in Phase 1 - see <c>.claude/PLAN.md</c>.
	///
	/// Members are grouped to match <see cref="INotificationSink"/>. Keep them in that order; it makes diffing
	/// against the interface tractable as Epic adds to it.
	/// </remarks>
	public sealed class DiscordNotificationSink : INotificationSink
	{
		readonly DiscordServerConfig _serverConfig;
		readonly ILogger<DiscordNotificationSink> _logger;

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="serverConfig">Server configuration for the Discord plugin.</param>
		/// <param name="logger">Logger for output.</param>
		public DiscordNotificationSink(IOptions<DiscordServerConfig> serverConfig, ILogger<DiscordNotificationSink> logger)
		{
			_serverConfig = serverConfig.Value;
			_logger = logger;

			// Logged at information because it is the signal that Phase 0 wiring works end to end. Once delivery
			// exists this should drop to debug, or move behind the client's own connection logging.
			if (_serverConfig.IsConfigured)
			{
				_logger.LogInformation("Discord notification sink registered (guild {GuildId}, interactions {Interactions})",
					_serverConfig.GuildId ?? "<unset>", _serverConfig.EnableInteractions ? "enabled" : "disabled");
			}
			else
			{
				_logger.LogInformation("Discord notification sink registered but no bot token is configured; notifications will be discarded");
			}
		}

		#region Jobs

		/// <inheritdoc/>
		public Task NotifyJobScheduledAsync(List<JobScheduledNotification> notifications, CancellationToken cancellationToken)
		{
			_logger.LogDebug("[Discord] JobScheduled ({Count} notifications)", notifications.Count);
			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public Task NotifyJobCompleteAsync(IJob job, IGraph graph, LabelOutcome outcome, CancellationToken cancellationToken)
		{
			_logger.LogDebug("[Discord] JobComplete {JobId} ({Outcome})", job.Id, outcome);
			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public Task NotifyJobCompleteAsync(IUser user, IJob job, IGraph graph, LabelOutcome outcome, CancellationToken cancellationToken)
		{
			_logger.LogDebug("[Discord] JobComplete {JobId} ({Outcome}) for user {UserId}", job.Id, outcome, user.Id);
			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public Task NotifyJobStepAbortedAsync(IEnumerable<IUser>? usersToNotify, IJob job, IJobStepBatch batch, IJobStep step, INode node, List<ILogEventData> jobStepEventData, CancellationToken cancellationToken)
		{
			_logger.LogDebug("[Discord] JobStepAborted {JobId}:{StepId}", job.Id, step.Id);
			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public Task NotifyJobStepCompleteAsync(IEnumerable<IUser>? usersToNotify, IJob job, IJobStepBatch batch, IJobStep step, INode node, List<ILogEventData> jobStepEventData, CancellationToken cancellationToken)
		{
			_logger.LogDebug("[Discord] JobStepComplete {JobId}:{StepId}", job.Id, step.Id);
			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public Task NotifyLabelCompleteAsync(IUser user, IJob job, ILabel label, int labelIdx, LabelOutcome outcome, List<(string, JobStepOutcome, Uri)> stepData, CancellationToken cancellationToken)
		{
			_logger.LogDebug("[Discord] LabelComplete {JobId} label {LabelIdx} ({Outcome})", job.Id, labelIdx, outcome);
			return Task.CompletedTask;
		}

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
		{
			_logger.LogDebug("[Discord] ConfigUpdate");
			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public Task NotifyConfigUpdateFailureAsync(string errorMessage, string fileName, int? change = null, IUser? author = null, string? description = null, CancellationToken cancellationToken = default)
		{
			_logger.LogDebug("[Discord] ConfigUpdateFailure ({FileName})", fileName);
			return Task.CompletedTask;
		}

		#endregion

		#region Farm operations

		/// <inheritdoc/>
		public Task NotifyDeviceServiceAsync(string message, IDevice? device = null, IDevicePool? pool = null, StreamConfig? streamConfig = null, IJob? job = null, IJobStep? step = null, INode? node = null, IUser? user = null, CancellationToken cancellationToken = default)
		{
			_logger.LogDebug("[Discord] DeviceService");
			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public Task SendDeviceIssueReportAsync(DeviceIssueReport report, CancellationToken cancellationToken)
		{
			_logger.LogDebug("[Discord] DeviceIssueReport");
			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public Task SendAgentReportAsync(AgentReport report, CancellationToken cancellationToken)
		{
			_logger.LogDebug("[Discord] AgentReport");
			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public Task SendSessionConflictReportAsync(IReadOnlyList<(AgentId Id, int Count)> conflicts, CancellationToken cancellationToken)
		{
			_logger.LogDebug("[Discord] SessionConflictReport ({Count} agents)", conflicts.Count);
			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public Task NotifyTestHealthReportAsync(ITestHealthReport report, string recipient, string[]? carbonCopies, CancellationToken cancellationToken)
		{
			_logger.LogDebug("[Discord] TestHealthReport (recipient {Recipient})", recipient);
			return Task.CompletedTask;
		}

		#endregion

		#region Links

		/// <inheritdoc/>
		/// <remarks>Returns null until Phase 3; callers treat a null link as "no deep link available".</remarks>
		public Task<string?> GetDirectMessageLinkAsync(IReadOnlyList<UserId> userIds, CancellationToken cancellationToken = default)
			=> Task.FromResult<string?>(null);

		/// <inheritdoc/>
		/// <remarks>Returns null until Phase 3; callers treat a null link as "no deep link available".</remarks>
		public Task<string?> GetChannelLinkAsync(string channel, CancellationToken cancellationToken = default)
			=> Task.FromResult<string?>(null);

		#endregion
	}
}
