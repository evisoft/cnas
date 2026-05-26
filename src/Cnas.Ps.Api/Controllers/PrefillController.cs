using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Prefill;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0552 / R0562 / TOR CF 06.03 + CF 07.03 — REST surface for the pre-fill API.
/// Two endpoints:
/// <list type="bullet">
///   <item><c>POST /api/self-service/prefill</c> — the caller's own data
///   (UC06 citizen self-service path, R0552).</item>
///   <item><c>POST /api/admin/prefill/{solicitantSqid}</c> — the explicit-solicitant
///   variant gated by the <c>Prefill.ForAnyApplicant</c> permission (UC07 staff
///   path, R0562).</item>
/// </list>
/// Both endpoints share the same wire shape and the same merge logic — only the
/// target Solicitant resolution and the permission gate differ.
/// </summary>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
public sealed class PrefillController : ControllerBase
{
    private readonly IPrefillService _prefill;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the controller with its collaborators.</summary>
    /// <param name="prefill">R0552 / R0562 pre-fill service.</param>
    /// <param name="sqids">Sqid decoder for the staff-route parameter.</param>
    public PrefillController(IPrefillService prefill, ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(prefill);
        ArgumentNullException.ThrowIfNull(sqids);
        _prefill = prefill;
        _sqids = sqids;
    }

    /// <summary>
    /// R0552 — pre-fill the calling citizen's own application form.
    /// </summary>
    /// <param name="request">Optional source / field allow-list.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// 200 OK with the populated <see cref="PrefillPayloadDto"/>;
    /// 401 when the caller is anonymous;
    /// 404 when no Solicitant is linked to the caller;
    /// 400 on validation failure.
    /// </returns>
    [HttpPost("api/self-service/prefill")]
    public async Task<ActionResult<PrefillPayloadDto>> PrefillForCurrentUserAsync(
        [FromBody] PrefillRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _prefill
            .PrefillForCurrentUserAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0562 — staff path: pre-fill the application form on behalf of the
    /// supplied applicant. Gated by the <c>Prefill.ForAnyApplicant</c> permission.
    /// </summary>
    /// <param name="solicitantSqid">Sqid-encoded Solicitant id.</param>
    /// <param name="request">Optional source / field allow-list.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// 200 OK with the populated <see cref="PrefillPayloadDto"/>;
    /// 400 ProblemDetails when the Sqid is malformed;
    /// 403 when the caller lacks the staff permission;
    /// 404 when the Solicitant does not exist.
    /// </returns>
    [HttpPost("api/admin/prefill/{solicitantSqid}")]
    public async Task<ActionResult<PrefillPayloadDto>> PrefillForSolicitantAsync(
        string solicitantSqid,
        [FromBody] PrefillRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solicitantSqid);
        ArgumentNullException.ThrowIfNull(request);

        var decoded = _sqids.TryDecode(solicitantSqid);
        if (decoded.IsFailure)
        {
            return MapFailure(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _prefill
            .PrefillForSolicitantAsync(decoded.Value, request, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Maps a service-level failure to the matching ProblemDetails ActionResult.
    /// <see cref="ErrorCodes.NotFound"/> → 404;
    /// <see cref="ErrorCodes.Unauthorized"/> → 401;
    /// <see cref="ErrorCodes.Forbidden"/> → 403; everything else → 400.
    /// </summary>
    /// <param name="errorCode">Stable error code from the service.</param>
    /// <param name="errorMessage">Human-readable description.</param>
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
            Title = "Pre-fill request rejected.",
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
