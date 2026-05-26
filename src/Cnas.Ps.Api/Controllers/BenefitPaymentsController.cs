using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Benefits;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0517 / TOR CF 02.05 — authenticated self-service surface for the citizen
/// "status of benefit payments" page. Two endpoints:
/// <list type="bullet">
///   <item><c>GET /api/self-service/benefit-payments</c> — the caller's own
///   ledger (Solicitant resolved server-side).</item>
///   <item><c>GET /api/admin/benefit-payments/{solicitantSqid}</c> — the
///   explicit-solicitant variant, gated by the
///   <c>BenefitPayment.ReadAny</c> permission.</item>
/// </list>
/// Both endpoints emit the same wire shape so a citizen-facing UI and a
/// back-office assistance UI can share a single rendering component.
/// </summary>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
public sealed class BenefitPaymentsController : ControllerBase
{
    private readonly IBenefitPaymentStatusService _status;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the controller with its service collaborators.</summary>
    /// <param name="status">R0517 benefit-payment status service.</param>
    /// <param name="sqids">Sqid decoder for the admin route parameter.</param>
    public BenefitPaymentsController(
        IBenefitPaymentStatusService status,
        ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(sqids);
        _status = status;
        _sqids = sqids;
    }

    /// <summary>
    /// R0517 — returns the benefit-payment status payload for the calling user.
    /// </summary>
    /// <param name="fromMonth">Optional inclusive lower bound (any day in the target month).</param>
    /// <param name="toMonth">Optional inclusive upper bound (any day in the target month).</param>
    /// <param name="type">Optional benefit-type filter (stable enum name, e.g. <c>OldAgePension</c>).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// 200 OK with the populated <see cref="BenefitPaymentStatusDto"/>;
    /// 400 ProblemDetails on validation failure; 401 when the caller is
    /// anonymous; 404 when no Solicitant is on file for the caller.
    /// </returns>
    [HttpGet("api/self-service/benefit-payments")]
    public async Task<ActionResult<BenefitPaymentStatusDto>> GetMineAsync(
        [FromQuery] DateOnly? fromMonth = null,
        [FromQuery] DateOnly? toMonth = null,
        [FromQuery] string? type = null,
        CancellationToken cancellationToken = default)
    {
        var query = new BenefitPaymentStatusQueryDto(fromMonth, toMonth, type);
        var result = await _status
            .GetForCurrentUserAsync(query, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0517 — admin / utilizator-autorizat variant: returns the
    /// benefit-payment status payload for the supplied Solicitant.
    /// </summary>
    /// <param name="solicitantSqid">Sqid-encoded Solicitant identifier.</param>
    /// <param name="fromMonth">Optional inclusive lower bound.</param>
    /// <param name="toMonth">Optional inclusive upper bound.</param>
    /// <param name="type">Optional benefit-type filter.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// 200 OK with the populated <see cref="BenefitPaymentStatusDto"/>;
    /// 400 ProblemDetails when the Sqid or query envelope is malformed;
    /// 403 ProblemDetails when the caller lacks the
    /// <c>BenefitPayment.ReadAny</c> permission.
    /// </returns>
    [HttpGet("api/admin/benefit-payments/{solicitantSqid}")]
    public async Task<ActionResult<BenefitPaymentStatusDto>> GetForSolicitantAsync(
        string solicitantSqid,
        [FromQuery] DateOnly? fromMonth = null,
        [FromQuery] DateOnly? toMonth = null,
        [FromQuery] string? type = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solicitantSqid);

        var decoded = _sqids.TryDecode(solicitantSqid);
        if (decoded.IsFailure)
        {
            return MapFailure(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var query = new BenefitPaymentStatusQueryDto(fromMonth, toMonth, type);
        var result = await _status
            .GetForSolicitantAsync(decoded.Value, query, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Maps a service-level <see cref="Result"/> failure to the appropriate
    /// ProblemDetails ActionResult. <see cref="ErrorCodes.NotFound"/> → 404;
    /// <see cref="ErrorCodes.Unauthorized"/> → 401;
    /// <see cref="ErrorCodes.Forbidden"/> → 403; everything else → 400.
    /// </summary>
    /// <param name="errorCode">Stable error code from the service.</param>
    /// <param name="errorMessage">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ObjectResult MapFailure(string? errorCode, string? errorMessage)
    {
        var status = errorCode switch
        {
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest,
        };
        var problem = new ProblemDetails
        {
            Status = status,
            Title = "Benefit-payment status rejected.",
            Detail = errorMessage,
        };
        problem.Extensions["errorCode"] = errorCode;
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
