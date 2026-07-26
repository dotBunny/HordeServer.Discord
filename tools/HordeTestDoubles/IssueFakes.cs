// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using EpicGames.Horde.Commits;
using EpicGames.Horde.Issues;
using EpicGames.Horde.Jobs;
using EpicGames.Horde.Jobs.Graphs;
using EpicGames.Horde.Jobs.Templates;
using EpicGames.Horde.Logs;
using EpicGames.Horde.Streams;
using EpicGames.Horde.Users;
using HordeServer.Issues;
using MongoDB.Bson;

namespace HordeTestDoubles
{
	/// <summary>
	/// Stand-ins for the issue types the triage notifications arrive with.
	/// </summary>
	/// <remarks>
	/// Here rather than beside the tests for the reason in <c>CLAUDE.md</c>: MSTest resolves the interfaces of every
	/// type the test assembly declares during discovery, before the engine assembly resolver is installed, and a
	/// fake implementing <see cref="IIssue"/> there would take the whole run down with it.
	/// </remarks>
	public static class IssueFakes
	{
		/// <summary>
		/// An open, unassigned issue.
		/// </summary>
		/// <param name="id">Issue number, which appears in the message and in every button's custom id.</param>
		/// <param name="summary">One-line description.</param>
		/// <param name="streams">Streams the issue affects.</param>
		public static FakeIssue Issue(int id, string summary, params string[] streams)
			=> new FakeIssue(id, summary, streams);

		/// <summary>
		/// A report for one stream and workflow.
		/// </summary>
		/// <param name="stream">Stream the report covers.</param>
		/// <param name="workflow">Workflow within it.</param>
		/// <param name="triageChannel">Slack channel id Horde would triage this in.</param>
		/// <param name="steps">Total steps in the window.</param>
		/// <param name="passingSteps">How many of them passed.</param>
		public static IssueReport Report(string stream, string workflow, string? triageChannel, int steps, int passingSteps)
			=> new IssueReport(
				new StreamId(stream),
				new WorkflowId(workflow),
				new WorkflowStats { NumSteps = steps, NumPassingSteps = passingSteps },
				triageChannel,
				false);
	}

	/// <summary>
	/// A mutable <see cref="IIssue"/>, so a test can describe the state it cares about and ignore the rest.
	/// </summary>
	public sealed class FakeIssue : IIssue
	{
		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="id">Issue number.</param>
		/// <param name="summary">One-line description.</param>
		/// <param name="streams">Streams the issue affects.</param>
		public FakeIssue(int id, string summary, params string[] streams)
		{
			Id = id;
			Summary = summary;
			Streams = [.. streams.Select(x => new FakeIssueStream(new StreamId(x)))];
		}

		public int Id { get; }

		public string Summary { get; set; }

		public string? UserSummary { get; set; }

		public string? Description { get; set; }

		public IReadOnlyList<IIssueFingerprint> Fingerprints { get; } = [];

		public IssueSeverity Severity { get; set; } = IssueSeverity.Error;

		public bool Promoted { get; set; }

		public UserId? OwnerId { get; set; }

		public UserId? NominatedById { get; set; }

		public DateTime CreatedAt { get; set; } = new DateTime(2026, 7, 26, 9, 0, 0, DateTimeKind.Utc);

		public DateTime? NominatedAt { get; set; }

		public DateTime? AcknowledgedAt { get; set; }

		public DateTime? ResolvedAt { get; set; }

		public UserId? ResolvedById { get; set; }

		public DateTime? VerifiedAt { get; set; }

		public DateTime LastSeenAt { get; set; } = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

		public CommitId? FixCommitId { get; set; }

		public CommitId? RootCommitId { get; set; }

		public UserId? RootCauseOwnerId { get; set; }

		public string? RootCauseCategory { get; set; }

		public string? RootCauseSummary { get; set; }

		public int? DuplicateIssueId { get; set; }

		public bool FixSystemic { get; set; }

		public UserId? QuarantinedByUserId { get; set; }

		public DateTime? QuarantineTimeUtc { get; set; }

		public UserId? ForceClosedByUserId { get; set; }

		public IReadOnlyList<IIssueStream> Streams { get; set; }

		public List<ObjectId>? ExcludeSpans { get; set; }

		public int UpdateIndex { get; set; }

		public string? ExternalIssueKey { get; set; }

		/// <summary>
		/// Where the triage conversation for this issue lives.
		/// </summary>
		/// <remarks>
		/// Horde stores one URL per issue, and the Slack sink puts its triage thread there. The Discord sink does
		/// not write it yet - that arrives with the thread work in PLAN.md 3.3.6, alongside the message-state
		/// collection - so this exists to satisfy the interface.
		/// </remarks>
		public Uri? WorkflowThreadUrl { get; set; }
	}

	/// <summary>
	/// One stream an issue was seen in.
	/// </summary>
	public sealed class FakeIssueStream : IIssueStream
	{
		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="streamId">Stream the issue was seen in.</param>
		public FakeIssueStream(StreamId streamId) => StreamId = streamId;

		public StreamId StreamId { get; }

		public bool? MergeOrigin { get; set; }

		public bool? ContainsFix { get; set; }

		public bool? FixFailed { get; set; }
	}

	/// <summary>
	/// One span of an issue - a stream, a template, and the step that failed.
	/// </summary>
	/// <remarks>
	/// Only the three things routing reads are settable. Everything else throws rather than returning a plausible
	/// default, so a test that starts depending on the rest fails loudly instead of asserting against invented data.
	/// </remarks>
	public sealed class FakeIssueSpan : IIssueSpan
	{
		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="streamId">Stream the failure was seen in.</param>
		/// <param name="templateId">Template that ran it.</param>
		/// <param name="workflowId">Workflow named by the failing step's annotations, if any.</param>
		public FakeIssueSpan(string streamId, string templateId = "build", string? workflowId = null)
		{
			StreamId = new StreamId(streamId);
			TemplateRefId = new TemplateId(templateId);
			LastFailure = new FakeIssueStep(workflowId);
		}

		public StreamId StreamId { get; }

		public TemplateId TemplateRefId { get; }

		public IIssueStep LastFailure { get; }

		public ObjectId Id => ObjectId.Empty;

		public string StreamName => StreamId.ToString();

		public string NodeName => "Compile";

		public int IssueId { get; set; }

		public bool PromoteByDefault => false;

		public int? MaxSuspectRank => null;

		public IIssueFingerprint Fingerprint => throw new NotSupportedException();

		public IIssueStep? LastSuccess => null;

		public IIssueStep FirstFailure => LastFailure;

		public IIssueStep? NextSuccess => null;

		public IReadOnlyList<IIssueSpanSuspect> Suspects => Array.Empty<IIssueSpanSuspect>();
	}

	/// <summary>
	/// The failing step of a span, carrying the annotations that choose a triage workflow.
	/// </summary>
	public sealed class FakeIssueStep : IIssueStep
	{
		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="workflowId">Workflow this step's node is annotated with, if any.</param>
		public FakeIssueStep(string? workflowId = null)
			=> Annotations = new NodeAnnotations { WorkflowId = workflowId == null ? null : new WorkflowId(workflowId) };

		public IReadOnlyNodeAnnotations Annotations { get; }

		public ObjectId SpanId => ObjectId.Empty;

		public IssueSeverity Severity => IssueSeverity.Error;

		public string JobName => "Incremental Build";

		public JobId JobId => default;

		public JobStepBatchId BatchId => default;

		public JobStepId StepId => default;

		public DateTime StepTime => DateTime.UnixEpoch;

		public bool PromoteByDefault => false;

		public LogId? LogId => null;

		public CommitIdWithOrder CommitId => throw new NotSupportedException();
	}
}
