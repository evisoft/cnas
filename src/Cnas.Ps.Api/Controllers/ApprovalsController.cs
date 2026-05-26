using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0590 / TOR CF 10.01 — REST surface backing the decider's approval-workspace
/// UI (<c>/approvals</c>). Two endpoints: the chip-strip summary aggregate and
/// the paged pending-decision list. Both are read-only projections of dossiers
/// in the <c>PendingApproval</c> state and require the
/// <see cref="AuthorizationComposition.CnasDecider"/> policy (cnas-decider or
/// cnas-admin role).
/// </summary>
/// <remarks>
/// <para>
/// <b>Approve / Reject actions.</b> The mutating actions invoked from the
/// workspace UI live on <see cref="DecisionsController"/> (POST
/// <c>/api/decisions/{sqid}/approve</c> and <c>.../reject</c>). This controller
/// is read-only — the wire surface that produces the list of work intentionally
/// does not also expose the action endpoints so the security review can audit
/// each surface independently.
/// </para>
/// </remarks>
/// <param name="svc">Approval-workspace projection service (per-request scope).</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasDecider)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/approvals")]
public sealed class ApprovalsController(IApprovalWorkspaceService svc) : ControllerBase
{
    private readonly IApprovalWorkspaceService _svc = svc;

    /// <summary>
    /// R0590 / TOR CF 10.01 — returns the chip-strip summary rendered above the
    /// pending-decisions list: total pending decisions, the subset whose SLA
    /// has lapsed, and the subset that landed on the queue today (UTC).
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// <c>200 OK</c> with an <see cref="ApprovalWorkspaceSummaryDto"/> body on a
    /// successful read; <c>400</c> ProblemDetails on any service-layer failure.
    /// </returns>
    [HttpGet("summary")]
    public async Task<ActionResult<ApprovalWorkspaceSummaryDto>> GetSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.GetSummaryAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0590 / TOR CF 10.01 — returns the paged list of decisions awaiting the
    /// caller's approval, ordered by SLA urgency (deadline ascending). All ids
    /// on the wire are Sqid-encoded.
    /// </summary>
    /// <param name="page">1-based page number. Defaults to 1.</param>
    /// <param name="pageSize">Items per page. Clamped to [1, 100].</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// <c>200 OK</c> with a <see cref="PagedResult{ApprovalQueueItemDto}"/> body
    /// on a successful read; <c>400</c> ProblemDetails on any service-layer
    /// failure.
    /// </returns>
    [HttpGet("pending")]
    public async Task<ActionResult<PagedResult<ApprovalQueueItemDto>>> ListPendingAsync(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ListPendingAsync(page, pageSize, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a service-layer failure to the matching HTTP status.</summary>
    /// <param name="errorCode">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="errorMessage">Human-readable message.</param>
    /// <returns>The matching <see cref="ObjectResult"/>.</returns>
    private ObjectResult MapFailure(string? errorCode, string? errorMessage)
    {
        var status = errorCode switch
        {
            ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest,
        };
        return Problem(errorMessage, statusCode: status);
    }
}
