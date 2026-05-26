using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R0590 / TOR CF 10.01 — read-only projection backing the decider's approval
/// workspace UI. The workspace surfaces two distinct queries: a small chip-strip
/// summary (<see cref="GetSummaryAsync"/>) and a paged list of the open approval
/// queue (<see cref="ListPendingAsync"/>). Both projections honour the caller's
/// access scope so a regional decider only sees dossiers from their subdivision
/// allow-list (R0671 / TOR CF 18.06).
/// </summary>
/// <remarks>
/// <para>
/// <b>Source of truth.</b> The approval queue is the set of dossiers whose parent
/// <c>ServiceApplication.Status == PendingApproval</c>. UC08.06 transitions a
/// dossier into <c>PendingApproval</c> when an examiner forwards it via
/// <c>IDocumentExaminationService.SubmitForApprovalAsync</c>; UC10
/// (<see cref="IDecisionWorkflowService"/>) flips the dossier out of that state
/// when the decider approves or rejects.
/// </para>
/// <para>
/// <b>Read-only.</b> This service performs no state mutations and emits no audit
/// records. Mutations are the job of <see cref="IDecisionWorkflowService"/>;
/// audit responsibility lives there too. The workspace service simply projects
/// the current state of the queue into wire DTOs.
/// </para>
/// <para>
/// <b>Read-replica safe.</b> Implementations route through
/// <c>IReadOnlyCnasDbContext</c> so the workspace can be served from the Postgres
/// streaming replica. Replica lag (≤ hundreds of ms) is acceptable for a
/// triage UI.
/// </para>
/// </remarks>
public interface IApprovalWorkspaceService
{
    /// <summary>
    /// R0590 / TOR CF 10.01 — produces the chip-strip summary rendered above the
    /// pending-decisions list: total pending decisions, subset whose SLA deadline
    /// has lapsed, and subset that landed on the queue today (UTC).
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> wrapping the summary DTO on a successful read.
    /// Failure shapes are reserved (the read path has no domain failures today) —
    /// implementations may surface <see cref="ErrorCodes.Internal"/> if the
    /// underlying read fails.
    /// </returns>
    Task<Result<ApprovalWorkspaceSummaryDto>> GetSummaryAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// R0590 / TOR CF 10.01 — paged list of decisions awaiting the caller's
    /// approval. Rows are ordered by SLA urgency (deadline ascending; rows
    /// without a deadline sort last) then by emission time. All ids on the wire
    /// are Sqid-encoded.
    /// </summary>
    /// <param name="page">1-based page number. Values below 1 are clamped to 1.</param>
    /// <param name="pageSize">Items per page. Clamped to the inclusive range [1, 100].</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> wrapping the paged result on a successful read.
    /// </returns>
    Task<Result<PagedResult<ApprovalQueueItemDto>>> ListPendingAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}
