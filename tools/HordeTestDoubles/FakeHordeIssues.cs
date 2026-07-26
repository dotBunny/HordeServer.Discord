// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using EpicGames.Horde.Commits;
using EpicGames.Horde.Users;
using HordeServer.Discord.Notifications;
using HordeServer.Issues;

namespace HordeTestDoubles
{
	/// <summary>
	/// One update a triage button asked for.
	/// </summary>
	/// <param name="Verb">Which operation was called.</param>
	/// <param name="IssueId">Issue it was called on.</param>
	/// <param name="UserId">Who it was attributed to.</param>
	/// <param name="TakeOwnership">Whether an acknowledgement also claimed the issue.</param>
	/// <param name="FixCommitId">Fix commit, for a resolve.</param>
	/// <param name="RootCauseSummary">Root cause summary, for a resolve.</param>
	/// <param name="RootCauseCommitId">Root cause commit, for a resolve.</param>
	/// <param name="DuplicateIssueId">Duplicate issue, for a resolve.</param>
	/// <param name="Category">Category, for a categorisation.</param>
	public sealed record HordeIssueUpdate(
		string Verb,
		int IssueId,
		UserId UserId,
		bool TakeOwnership = false,
		CommitId? FixCommitId = null,
		string? RootCauseSummary = null,
		CommitId? RootCauseCommitId = null,
		int? DuplicateIssueId = null,
		string? Category = null);

	/// <summary>
	/// An issue service that records what it was asked to do instead of doing it.
	/// </summary>
	/// <remarks>
	/// The seam that keeps triage testable. The real implementation is a handful of calls into Horde's
	/// <c>IssueService</c>, which reaches MongoDB in its constructor - so what is worth asserting is not the update
	/// itself but that the right operation was called, attributed to the right person, with the values the operator
	/// actually typed.
	/// </remarks>
	public sealed class FakeHordeIssues : IHordeIssues
	{
		readonly Dictionary<int, IIssue> _issues = new Dictionary<int, IIssue>();

		/// <summary>
		/// Every update that was asked for, in order.
		/// </summary>
		public List<HordeIssueUpdate> Updates { get; } = new List<HordeIssueUpdate>();

		/// <summary>
		/// Whether updates should report success.
		/// </summary>
		public bool Succeeds { get; set; } = true;

		/// <summary>
		/// The most recent update, for the common single-action assertion.
		/// </summary>
		public HordeIssueUpdate Last => Updates[^1];

		/// <summary>
		/// Makes an issue findable.
		/// </summary>
		public FakeIssue Add(FakeIssue issue)
		{
			_issues[issue.Id] = issue;
			return issue;
		}

		/// <summary>
		/// The issue as the fake holds it, for a test that wants to change it between notifications.
		/// </summary>
		public FakeIssue Get(int issueId) => (FakeIssue)_issues[issueId];

		public Task<IIssue?> GetAsync(int issueId, CancellationToken cancellationToken)
			=> Task.FromResult(_issues.GetValueOrDefault(issueId));

		public Task<bool> AcknowledgeAsync(int issueId, UserId userId, bool takeOwnership, CancellationToken cancellationToken)
		{
			Updates.Add(new HordeIssueUpdate("acknowledge", issueId, userId, TakeOwnership: takeOwnership));
			return Task.FromResult(Succeeds);
		}

		public Task<bool> DeclineAsync(int issueId, UserId userId, CancellationToken cancellationToken)
		{
			Updates.Add(new HordeIssueUpdate("decline", issueId, userId));
			return Task.FromResult(Succeeds);
		}

		public Task<bool> ResolveAsync(
			int issueId,
			UserId userId,
			CommitId? fixCommitId,
			string? rootCauseSummary,
			CommitId? rootCauseCommitId,
			int? duplicateIssueId,
			CancellationToken cancellationToken)
		{
			Updates.Add(new HordeIssueUpdate(
				"resolve",
				issueId,
				userId,
				FixCommitId: fixCommitId,
				RootCauseSummary: rootCauseSummary,
				RootCauseCommitId: rootCauseCommitId,
				DuplicateIssueId: duplicateIssueId));

			return Task.FromResult(Succeeds);
		}

		public Task<bool> SetRootCauseCategoryAsync(int issueId, UserId userId, string category, CancellationToken cancellationToken)
		{
			Updates.Add(new HordeIssueUpdate("category", issueId, userId, Category: category));
			return Task.FromResult(Succeeds);
		}

		/// <summary>
		/// Thread urls that were written back, by issue.
		/// </summary>
		public Dictionary<int, Uri> ThreadUrls { get; } = new Dictionary<int, Uri>();

		public Task<bool> SetWorkflowThreadUrlAsync(int issueId, Uri threadUrl, CancellationToken cancellationToken)
		{
			ThreadUrls[issueId] = threadUrl;

			// Also reflected onto the issue, so a second notification sees what the first stored - which is the
			// whole behaviour under test.
			if (_issues.GetValueOrDefault(issueId) is FakeIssue issue)
			{
				issue.WorkflowThreadUrl = threadUrl;
			}

			return Task.FromResult(Succeeds);
		}
	}
}
