using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0058 / SEC 027 — maker-checker / 4-eyes admin actions REST surface. All endpoints
/// are gated by <see cref="AuthorizationComposition.CnasAdmin"/>. Two admins are
/// required to land a sensitive action: the maker submits (existing per-action
/// endpoints, retrofitted gradually), and any OTHER administrator visits this
/// controller to approve or reject the pending row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>GET    /api/admin/pending-actions</c>             — paged list of still-pending actions.</item>
///   <item><c>POST   /api/admin/pending-actions/{id}/approve</c> — 204 / 400 / 403 / 404 / 409.</item>
///   <item><c>POST   /api/admin/pending-actions/{id}/reject</c>  — 204 / 400 / 403 / 404 / 409.</item>
/// </list>
/// </para>
/// <para>
/// <b>Error-code → HTTP status mapping.</b>
/// <see cref="ErrorCodes.NotFound"/> → 404,
/// <see cref="ErrorCodes.Forbidden"/> + <see cref="ErrorCodes.MakerCheckerSelfApprovalForbidden"/> → 403,
/// <see cref="ErrorCodes.Unauthorized"/> → 401,
/// <see cref="ErrorCodes.MakerCheckerAlreadyDecided"/> + <see cref="ErrorCodes.MakerCheckerExpired"/> → 409,
/// every other code → 400.
/// </para>
/// </remarks>
/// <param name="svc">Underlying maker-checker service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/pending-actions")]
public sealed class PendingAdminActionsController(IPendingAdminActionService svc) : ControllerBase
{
    private readonly IPendingAdminActionService _svc = svc;

    /// <summary>
    /// Pages through the still-pending admin actions. Expired rows are filtered out so
    /// checkers don't waste a click on an action that the sweeper is about to close.
    /// </summary>
    /// <param name="page">1-based page number; defaults to 1.</param>
    /// <param name="pageSize">Items per page (service clamps to <c>[1, 200]</c>); defaults to 20.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with a paged list of <see cref="PendingAdminActionItem"/>; 400/403 on failure.</returns>
    [HttpGet]
    public async Task<ActionResult<PagedResult<PendingAdminActionItem>>> ListAsync(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ListPendingAsync(new PageRequest(page, pageSize), cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<PagedResult<PendingAdminActionItem>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Approves a pending admin action — the calling administrator becomes the
    /// checker. The service performs the maker ≠ checker, TTL, and already-decided
    /// guards before invoking the matching executor.
    /// </summary>
    /// <param name="id">Sqid-encoded id of the pending row.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 400 / 403 / 404 / 409 on failure per the controller remarks.</returns>
    [HttpPost("{id}/approve")]
    public async Task<IActionResult> ApproveAsync(
        [FromRoute] string id,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ApproveAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Rejects a pending admin action with a free-form reason. The executor is NOT
    /// invoked — rejection simply closes the row with
    /// <c>Status = Rejected</c> and persists the reason for the audit trail.
    /// </summary>
    /// <param name="id">Sqid-encoded id of the pending row.</param>
    /// <param name="body">Request payload carrying the rejection reason.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 400 / 403 / 404 / 409 on failure per the controller remarks.</returns>
    [HttpPost("{id}/reject")]
    [Consumes("application/json")]
    public async Task<IActionResult> RejectAsync(
        [FromRoute] string id,
        [FromBody] RejectAdminActionRequest body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await _svc.RejectAsync(id, body.Reason, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a generic <see cref="Result{T}"/> failure to an <see cref="ActionResult{T}"/>.</summary>
    /// <typeparam name="T">The DTO type the action would have returned on success.</typeparam>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 403 / 409 / 400 ProblemDetails as appropriate.</returns>
    private ActionResult<T> MapFailureGeneric<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Maps a non-generic <see cref="Result"/> failure to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 403 / 409 / 400 ProblemDetails as appropriate.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>Mapped HTTP status code.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.MakerCheckerSelfApprovalForbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.MakerCheckerAlreadyDecided => StatusCodes.Status409Conflict,
        ErrorCodes.MakerCheckerExpired => StatusCodes.Status409Conflict,
        ErrorCodes.MakerCheckerUnknownOperation => StatusCodes.Status400BadRequest,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
