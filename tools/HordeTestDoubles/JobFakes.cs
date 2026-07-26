// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EpicGames.Core;
using EpicGames.Horde.Acls;
using EpicGames.Horde.Agents;
using EpicGames.Horde.Agents.Leases;
using EpicGames.Horde.Agents.Pools;
using EpicGames.Horde.Agents.Sessions;
using EpicGames.Horde.Commits;
using EpicGames.Horde.Jobs;
using EpicGames.Horde.Jobs.Bisect;
using EpicGames.Horde.Jobs.Graphs;
using EpicGames.Horde.Jobs.Templates;
using EpicGames.Horde.Logs;
using EpicGames.Horde.Notifications;
using EpicGames.Horde.Streams;
using EpicGames.Horde.Users;
using HordeServer.Logs;
using Microsoft.Extensions.Logging;

namespace HordeTestDoubles
{
	/// <summary>
	/// A job with the handful of properties a notification reads, and nothing else.
	/// </summary>
	/// <remarks>
	/// <see cref="IJob"/> has forty-four members and a notification message reads eleven of them. The rest throw
	/// rather than returning a plausible default, so that a formatter quietly growing a dependency on something this
	/// fake never meant to describe fails loudly instead of rendering a lie.
	/// </remarks>
	public sealed class FakeJob : IJob
	{
		public JobId Id { get; set; } = JobId.Parse("65f0000000000000000000a1");

		public string Name { get; set; } = "Incremental Build";

		public StreamId StreamId { get; set; } = new StreamId("dethol-main");

		public CommitIdWithOrder CommitId { get; set; } = new CommitIdWithOrder("12345", 12345);

		public CommitId? PreflightCommitId { get; set; }

		public DateTime UpdateTimeUtc { get; set; } = new DateTime(2026, 7, 25, 14, 30, 0, DateTimeKind.Utc);

		public UserId? AbortedByUserId { get; set; }

		public string? CancellationReason { get; set; }

		public string? NotificationChannel { get; set; }

		public string? NotificationChannelFilter { get; set; }

		public TemplateId TemplateId { get; set; } = new TemplateId("incremental-build");

		public ContentHash? TemplateHash => throw new NotSupportedException();

		public ContentHash GraphHash => throw new NotSupportedException();

		public IGraph Graph => throw new NotSupportedException();

		public UserId? StartedByUserId => throw new NotSupportedException();

		public BisectTaskId? StartedByBisectTaskId => throw new NotSupportedException();

		public CommitIdWithOrder? CodeCommitId => throw new NotSupportedException();

		public string? PreflightDescription => throw new NotSupportedException();

		public Priority Priority => throw new NotSupportedException();

		public bool AutoSubmit => throw new NotSupportedException();

		public int? AutoSubmitChange => throw new NotSupportedException();

		public string? AutoSubmitMessage => throw new NotSupportedException();

		public bool UpdateIssues => throw new NotSupportedException();

		public bool PromoteIssuesByDefault => throw new NotSupportedException();

		public DateTime CreateTimeUtc => throw new NotSupportedException();

		public JobOptions? JobOptions => throw new NotSupportedException();

		public IReadOnlyList<IAclClaim> Claims => throw new NotSupportedException();

		public IReadOnlyList<IJobStepBatch> Batches => throw new NotSupportedException();

		public IReadOnlyDictionary<ParameterId, string> Parameters => throw new NotSupportedException();

		public IReadOnlyList<string> Arguments => throw new NotSupportedException();

		public IReadOnlyList<string>? Targets => throw new NotSupportedException();

		public IReadOnlyList<string> AdditionalArguments => throw new NotSupportedException();

		public IReadOnlyDictionary<string, string> Environment => throw new NotSupportedException();

		public IReadOnlyList<int> Issues => throw new NotSupportedException();

		public NotificationTriggerId? NotificationTriggerId => throw new NotSupportedException();

		public bool ShowUgsBadges => throw new NotSupportedException();

		public bool ShowUgsAlerts => throw new NotSupportedException();

		public IReadOnlyDictionary<int, NotificationTriggerId> LabelIdxToTriggerId => throw new NotSupportedException();

		public IReadOnlyList<IJobReport>? Reports => throw new NotSupportedException();

		public IReadOnlyList<IChainedJob> ChainedJobs => throw new NotSupportedException();

		public JobId? ParentJobId => throw new NotSupportedException();

		public JobStepId? ParentJobStepId => throw new NotSupportedException();

		public List<string> Metadata => throw new NotSupportedException();

		public int UpdateIndex => throw new NotSupportedException();

		// IJob is a live document as well as a description of one: it can refresh and mutate itself. A notification
		// only ever reads, so every one of these is an error rather than something to stub out.
		public Task<IJob?> RefreshAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public bool TryGetBatch(JobStepBatchId batchId, [NotNullWhen(true)] out IJobStepBatch? batch) => throw new NotSupportedException();

		public bool TryGetStep(JobStepId stepId, [NotNullWhen(true)] out IJobStep? step) => throw new NotSupportedException();

		public Task<bool> TryDeleteAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<IJob?> TryRemoveFromDispatchQueueAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<IJob?> TryUpdateJobAsync(string? name = null, Priority? priority = null, bool? autoSubmit = null, int? autoSubmitChange = null, string? autoSubmitMessage = null, UserId? abortedByUserId = null, NotificationTriggerId? notificationTriggerId = null, List<JobReport>? reports = null, List<string>? arguments = null, KeyValuePair<int, NotificationTriggerId>? labelIdxToTriggerId = null, KeyValuePair<TemplateId, JobId>? jobTrigger = null, string? cancellationReason = null, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<IJob?> TryUpdateBatchAsync(JobStepBatchId batchId, LogId? newLogId, JobStepBatchState? newState, JobStepBatchError? newError, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<IJob?> TryUpdateStepAsync(JobStepBatchId batchId, JobStepId stepId, JobStepState newState = default, JobStepOutcome newOutcome = default, JobStepError? newError = null, bool? newAbortRequested = null, UserId? newAbortByUserId = null, LogId? newLogId = null, NotificationTriggerId? newNotificationTriggerId = null, UserId? newRetryByUserId = null, Priority? newPriority = null, List<JobReport>? newReports = null, Dictionary<string, string?>? newProperties = null, string? newCancellationReason = null, JobId? newSpawnedJob = null, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<IJob?> TryUpdateGraphAsync(IGraph newGraph, CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<IJob?> TrySkipAllBatchesAsync(JobStepBatchError reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<IJob?> TrySkipBatchAsync(JobStepBatchId batchId, JobStepBatchError reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<IJob?> TryFailBatchAsync(int batchIdx, JobStepBatchError reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<IJob?> TryAssignLeaseAsync(int batchIdx, PoolId poolId, AgentId agentId, SessionId sessionId, LeaseId leaseId, LogId logId, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<IJob?> TryCancelLeaseAsync(int batchIdx, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	/// <summary>
	/// A job step with the properties a notification reads.
	/// </summary>
	public sealed class FakeJobStep : IJobStep
	{
		public JobStepId Id { get; set; } = JobStepId.Parse("a1b2");

		public JobStepOutcome Outcome { get; set; } = JobStepOutcome.Failure;

		public JobStepError Error { get; set; } = JobStepError.None;

		public DateTime? StartTimeUtc { get; set; } = new DateTime(2026, 7, 25, 14, 12, 0, DateTimeKind.Utc);

		public DateTime? FinishTimeUtc { get; set; } = new DateTime(2026, 7, 25, 14, 30, 0, DateTimeKind.Utc);

		public string? CancellationReason { get; set; }

		public IJob Job => throw new NotSupportedException();

		public IJobStepBatch Batch => throw new NotSupportedException();

		public INode Node => throw new NotSupportedException();

		public int NodeIdx => throw new NotSupportedException();

		public string Name => throw new NotSupportedException();

		public IReadOnlyList<JobStepOutputRef> Inputs => throw new NotSupportedException();

		public IReadOnlyList<JobStepOutputRef> OptionalInputs => throw new NotSupportedException();

		public IReadOnlyList<string> OutputNames => throw new NotSupportedException();

		public IReadOnlyList<JobStepId> InputDependencies => throw new NotSupportedException();

		public IReadOnlyList<JobStepId> OptionalInputDependencies => throw new NotSupportedException();

		public IReadOnlyList<JobStepId> OrderDependencies => throw new NotSupportedException();

		public bool AllowRetry => throw new NotSupportedException();

		public bool RunEarly => throw new NotSupportedException();

		public bool Warnings => throw new NotSupportedException();

		public IReadOnlyDictionary<string, string>? Credentials => throw new NotSupportedException();

		public IReadOnlyNodeAnnotations Annotations => throw new NotSupportedException();

		public IReadOnlyList<string> Metadata => throw new NotSupportedException();

		public JobStepState State => throw new NotSupportedException();

		public LogId? LogId => throw new NotSupportedException();

		public NotificationTriggerId? NotificationTriggerId => throw new NotSupportedException();

		public DateTime? ReadyTimeUtc => throw new NotSupportedException();

		public Priority? Priority => throw new NotSupportedException();

		public UserId? RetriedByUserId => throw new NotSupportedException();

		public bool AbortRequested => throw new NotSupportedException();

		public UserId? AbortedByUserId => throw new NotSupportedException();

		public IReadOnlyList<IJobReport>? Reports => throw new NotSupportedException();

		public IReadOnlyList<JobId>? SpawnedJobs => throw new NotSupportedException();

		public IReadOnlyDictionary<string, string>? Properties => throw new NotSupportedException();
	}

	/// <summary>
	/// A graph node, which a notification reads only for its name.
	/// </summary>
	public sealed class FakeNode : INode
	{
		public FakeNode(string name) => Name = name;

		public string Name { get; }

		public IReadOnlyList<NodeOutputRef> Inputs => throw new NotSupportedException();

		public IReadOnlyList<NodeOutputRef> OptionalInputs => throw new NotSupportedException();

		public IReadOnlyList<string> OutputNames => throw new NotSupportedException();

		public NodeRef[] InputDependencies => throw new NotSupportedException();

		public NodeRef[] OptionalInputDependencies => throw new NotSupportedException();

		public NodeRef[] OrderDependencies => throw new NotSupportedException();

		public Priority Priority => throw new NotSupportedException();

		public bool AllowRetry => throw new NotSupportedException();

		public bool RunEarly => throw new NotSupportedException();

		public bool Warnings => throw new NotSupportedException();

		public IReadOnlyDictionary<string, string>? Credentials => throw new NotSupportedException();

		public IReadOnlyDictionary<string, string>? Properties => throw new NotSupportedException();

		public IReadOnlyNodeAnnotations Annotations => throw new NotSupportedException();
	}

	/// <summary>
	/// A label, which a notification reads only for its display names.
	/// </summary>
	public sealed class FakeLabel : ILabel
	{
		public string? DashboardName { get; set; } = "Editor";

		public string? DashboardCategory { get; set; } = "Windows";

		public string? UgsName { get; set; }

		public string? UgsProject { get; set; }

		public LabelChange Change => throw new NotSupportedException();

		public List<NodeRef> RequiredNodes => throw new NotSupportedException();

		public List<NodeRef> IncludedNodes => throw new NotSupportedException();
	}

	/// <summary>
	/// A log event, which a notification reads for its severity and rendered message.
	/// </summary>
	public sealed class FakeLogEventData : ILogEventData
	{
		public FakeLogEventData(LogEventSeverity severity, string message)
		{
			Severity = severity;
			Message = message;
		}

		public LogEventSeverity Severity { get; }

		public string Message { get; }

		public EventId? EventId => throw new NotSupportedException();

		public IReadOnlyList<JsonLogEvent> Lines => throw new NotSupportedException();
	}
}
