using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.AccessScope;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0671 continuation / TOR CF 18.06 — admin REST surface for the access-scope
/// back-fill helper. Hangs off the <c>/api/admin/access-scope/</c> namespace
/// because the operation is a one-shot bulk-assignment of the columns
/// introduced by R0671; it has no read endpoint (operators inspect the result
/// in the audit log and the affected list views).
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation.</b> Every endpoint is gated by the <c>cnas-admin</c> role —
/// these operations rewrite security-relevant columns for thousands of rows in
/// one shot, so the gate is deliberately tight. Anonymous callers receive 401;
/// authenticated callers without the role receive 403.
/// </para>
/// <para>
/// <b>Error mapping.</b> The helper emits structured failures via the
/// <see cref="Result{T}"/> envelope; this controller routes the stable
/// <see cref="ErrorCodes"/> values to the canonical HTTP status: 400 for
/// validation / quota / branch-not-found, 404 only if the underlying helper
/// surfaces <see cref="ErrorCodes.NotFound"/> (it currently uses
/// <see cref="ErrorCodes.BranchNotFound"/> instead so a typo distinguishes from
/// a real 404).
/// </para>
/// </remarks>
/// <param name="svc">Underlying access-scope back-fill service.</param>
[ApiController]
[Authorize(Roles = "cnas-admin")]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/access-scope")]
public sealed class AccessScopeBackfillController(IAccessScopeBackfillService svc) : ControllerBase
{
    private readonly IAccessScopeBackfillService _svc = svc;

    /// <summary>
    /// Bulk-assigns <c>Solicitant.RegionCode</c> on the resolved row set.
    /// </summary>
    /// <param name="input">Selection + region-code envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the result DTO on success; ProblemDetails on failure.</returns>
    [HttpPost("solicitants/backfill-region")]
    public async Task<ActionResult<AccessScopeBackfillResultDto>> BackfillSolicitantRegionAsync(
        [FromBody] AccessScopeSolicitantBackfillInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _svc.AssignSolicitantRegionByPatternAsync(input, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Bulk-assigns <c>ServiceApplication.SubdivisionCode</c> on the resolved row set.
    /// </summary>
    /// <param name="input">Selection + subdivision-code envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the result DTO on success; ProblemDetails on failure.</returns>
    [HttpPost("applications/backfill-subdivision")]
    public async Task<ActionResult<AccessScopeBackfillResultDto>> BackfillApplicationSubdivisionAsync(
        [FromBody] AccessScopeApplicationBackfillInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _svc.AssignServiceApplicationSubdivisionByPatternAsync(input, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Routes a stable <see cref="ErrorCodes"/> value to a ProblemDetails-shaped
    /// failure response.
    /// </summary>
    /// <param name="code">Stable error code from the service.</param>
    /// <param name="message">Human-readable message.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ActionResult<AccessScopeBackfillResultDto> MapFailure(string? code, string? message)
    {
        var status = code switch
        {
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest,
        };
        return Problem(message, statusCode: status);
    }
}
