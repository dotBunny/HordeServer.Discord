// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using EpicGames.Horde.Commits;
using EpicGames.Horde.Users;
using HordeServer.Issues;
using Microsoft.Extensions.Logging;

namespace HordeServer.Discord.Notifications
{
	/// <summary>
	/// The issue operations a triage button performs.
	/// </summary>
	/// <remarks>
	/// A seam over Horde's <c>IssueService</c>, which is a concrete sealed class that reaches MongoDB in its
	/// constructor. Without this, nothing that acts on a button press could be tested here at all - the test suite is
	/// deliberately runnable with no MongoDB and no Redis, and that is the property being protected.
	///
	/// Narrow on purpose. It exposes the five things the buttons and the Mark Fixed modal do and nothing else, so the
	/// adapter behind it stays thin enough to read and be confident in without a test.
	/// </remarks>
	public interface IHordeIssues
	{
		/// <summary>
		/// Reads an issue back, to check who owns it and to re-render the message afterwards.
		/// </summary>
		/// <param name="issueId">Issue to fetch.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>The issue, or null if it no longer exists.</returns>
		Task<IIssue?> GetAsync(int issueId, CancellationToken cancellationToken);

		/// <summary>
		/// Marks an issue acknowledged, optionally taking ownership of it at the same time.
		/// </summary>
		/// <remarks>
		/// Ownership is separate because the two flavours differ: acknowledging a direct message means "yes, mine" on
		/// an issue already assigned to the reader, while pressing the same button in a channel is how somebody
		/// claims an unowned issue. Slack keeps two handlers for exactly this distinction.
		/// </remarks>
		/// <param name="issueId">Issue to acknowledge.</param>
		/// <param name="userId">Who is acknowledging it.</param>
		/// <param name="takeOwnership">Whether to also assign it to them.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>True if the issue was updated.</returns>
		Task<bool> AcknowledgeAsync(int issueId, UserId userId, bool takeOwnership, CancellationToken cancellationToken);

		/// <summary>
		/// Records that somebody is not responsible for an issue.
		/// </summary>
		/// <param name="issueId">Issue to decline.</param>
		/// <param name="userId">Who is declining it.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>True if the issue was updated.</returns>
		Task<bool> DeclineAsync(int issueId, UserId userId, CancellationToken cancellationToken);

		/// <summary>
		/// Resolves an issue, with whatever the Mark Fixed modal collected.
		/// </summary>
		/// <param name="issueId">Issue to resolve.</param>
		/// <param name="userId">Who is resolving it.</param>
		/// <param name="fixCommitId">Commit that fixed it, if one was given.</param>
		/// <param name="rootCauseSummary">Root cause summary, if one was written.</param>
		/// <param name="rootCauseCommitId">Commit that caused it, if one was given.</param>
		/// <param name="duplicateIssueId">Issue this duplicates, if one was given.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>True if the issue was updated.</returns>
		Task<bool> ResolveAsync(
			int issueId,
			UserId userId,
			CommitId? fixCommitId,
			string? rootCauseSummary,
			CommitId? rootCauseCommitId,
			int? duplicateIssueId,
			CancellationToken cancellationToken);

		/// <summary>
		/// Records the root cause category chosen from the follow-up dropdown.
		/// </summary>
		/// <param name="issueId">Issue to categorise.</param>
		/// <param name="userId">Who chose it.</param>
		/// <param name="category">Category chosen.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>True if the issue was updated.</returns>
		Task<bool> SetRootCauseCategoryAsync(int issueId, UserId userId, string category, CancellationToken cancellationToken);
	}

	/// <summary>
	/// <see cref="IHordeIssues"/> over Horde's own issue service.
	/// </summary>
	/// <remarks>
	/// **The one class in the plugin with no test coverage**, and kept trivial for that reason: every method is a
	/// single call with named arguments. <c>IssueService</c> is registered by the Build plugin, which is also where
	/// <c>INotificationSink</c> lives - so if it is absent, this plugin had nothing to do anyway.
	/// </remarks>
	public sealed class HordeIssues : IHordeIssues
	{
		readonly IssueService _issues;
		readonly ILogger _logger;

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="issues">Horde's issue service.</param>
		/// <param name="logger">Logger for failed updates.</param>
		public HordeIssues(IssueService issues, ILogger<HordeIssues> logger)
		{
			_issues = issues;
			_logger = logger;
		}

		/// <inheritdoc/>
		public Task<IIssue?> GetAsync(int issueId, CancellationToken cancellationToken)
			=> _issues.Collection.GetIssueAsync(issueId, cancellationToken);

		/// <inheritdoc/>
		public Task<bool> AcknowledgeAsync(int issueId, UserId userId, bool takeOwnership, CancellationToken cancellationToken)
			=> _issues.UpdateIssueAsync(
				issueId,
				acknowledged: true,
				ownerId: takeOwnership ? userId : null,
				nominatedById: takeOwnership ? userId : null,
				initiatedById: userId,
				cancellationToken: cancellationToken);

		/// <inheritdoc/>
		public Task<bool> DeclineAsync(int issueId, UserId userId, CancellationToken cancellationToken)
			=> _issues.UpdateIssueAsync(
				issueId,
				declinedById: userId,
				initiatedById: userId,
				cancellationToken: cancellationToken);

		/// <inheritdoc/>
		public Task<bool> ResolveAsync(
			int issueId,
			UserId userId,
			CommitId? fixCommitId,
			string? rootCauseSummary,
			CommitId? rootCauseCommitId,
			int? duplicateIssueId,
			CancellationToken cancellationToken)
			=> _issues.UpdateIssueAsync(
				issueId,
				resolvedById: userId,
				fixCommitId: fixCommitId,
				rootCauseSummary: rootCauseSummary,
				rootCommitId: rootCauseCommitId,
				duplicateIssueId: duplicateIssueId,
				initiatedById: userId,
				cancellationToken: cancellationToken);

		/// <inheritdoc/>
		public Task<bool> SetRootCauseCategoryAsync(int issueId, UserId userId, string category, CancellationToken cancellationToken)
			=> _issues.UpdateIssueAsync(
				issueId,
				rootCauseCategory: category,
				initiatedById: userId,
				cancellationToken: cancellationToken);
	}
}
