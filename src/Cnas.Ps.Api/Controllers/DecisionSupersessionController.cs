using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0933 / TOR §10.1 — REST surface backing the terminate-prior-on-acceptance
/// lifecycle. Two endpoints:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>GET /api/decisions/{sqid}/compare-with-prior</c> — read-only
///       projection of the (PriorAmount, NewAmount, Difference,
///       LowerSumWarning) tuple. The decider UI calls this BEFORE finalising
///       the new decision so the lower-sum warning can be acknowledged.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>POST /api/decisions/{sqid}/supersede-prior</c> — terminates the
///       prior active decision for the same (Solicitant, ServiceCode) pair and
///       records a <c>DecisionSupersession</c> row. Idempotent.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Authorization.</b> Both endpoints sit behind the
/// <see cref="AuthorizationComposition.CnasDecider"/> policy — only deciders
/// (or admins) may invoke them. The terminator service itself does NOT enforce
/// role checks; the policy on this controller is the security boundary.
/// </para>
/// <para>
/// <b>Failure mapping.</b>
/// </para>
/// <list type="bullet">
///   <item><see cref="ErrorCodes.NotFound"/> → 404.</item>
///   <item><see cref="ErrorCodes.InvalidSqid"/> → 400.</item>
///   <item>Anything else → 400.</item>
/// </list>
/// </remarks>
/// <param name="terminator">Per-request scoped terminator service.</param>
/// <param name="sqids">Sqid encoder/decoder for the route parameter.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasDecider)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/decisions")]
public sealed class DecisionSupersessionController(
    IPriorDecisionTerminator terminator,
    ISqidService sqids) : ControllerBase
{
    /// <summary>Underlying service.</summary>
    private readonly IPriorDecisionTerminator _terminator = terminator;

    /// <summary>Sqid encoder/decoder.</summary>
    private readonly ISqidService _sqids = sqids;

    /// <summary>
    /// R0933 — returns the comparison tuple between the new decision identified
    /// by <paramref name="sqid"/> and the most recent prior active decision for
    /// the same (Solicitant, ServiceCode) pair.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the new decision.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// <c>200 OK</c> + <see cref="DecisionComparisonDto"/> on success; failure
    /// mapping per the type-level remarks.
    /// </returns>
    [HttpGet("{sqid}/compare-with-prior")]
    public async Task<ActionResult<DecisionComparisonDto>> CompareWithPriorAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailure(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _terminator.CompareAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0933 — terminates the prior active decision for the same
    /// (Solicitant, ServiceCode) pair as the new decision identified by
    /// <paramref name="sqid"/>, and records an append-only supersession row.
    /// Idempotent — repeated calls for the same pair short-circuit on the
    /// existing row.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the new (just-approved) decision.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// <c>200 OK</c> + <see cref="DecisionSupersessionDto"/> when a prior was
    /// terminated (or already had been); <c>204 No Content</c> when no prior
    /// active decision exists; failure mapping per the type-level remarks.
    /// </returns>
    [HttpPost("{sqid}/supersede-prior")]
    public async Task<IActionResult> SupersedePriorAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailure(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _terminator.TerminateOnAcceptanceAsync(decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return MapFailure(result.ErrorCode, result.ErrorMessage);
        }

        return result.Value is null ? NoContent() : Ok(result.Value);
    }

    /// <summary>Maps a service-layer failure to the matching HTTP status.</summary>
    /// <param name="errorCode">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="errorMessage">Human-readable message.</param>
    /// <returns>The matching <see cref="ObjectResult"/>.</returns>
    private ObjectResult MapFailure(string? errorCode, string? errorMessage)
    {
        var status = errorCode switch
        {
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest,
        };
        var problem = new ProblemDetails
        {
            Title = errorCode ?? "Bad request",
            Detail = errorMessage,
            Status = status,
        };
        if (errorCode is not null)
        {
            problem.Extensions["errorCode"] = errorCode;
        }
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
