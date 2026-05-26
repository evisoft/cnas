using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.ApplicationProcessing;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0701 / TOR CF 21.01-02 — read-only HTTP surface that exposes the single-payload
/// "open application dossier" aggregator (<see cref="IApplicationProcessingContextService"/>).
/// The future CNAS staff processing UI (Blazor batch) calls this endpoint once to
/// populate the entire application detail screen instead of fanning out across N
/// parallel REST calls.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation.</b> The class is gated by <c>[Authorize(Roles="cnas-admin,cnas-user")]</c>
/// — the same broad role-band that already protects the existing staff-facing
/// registries. The fine-grained authorisation (admin OR assigned examiner OR holder
/// of the <c>Application.Process</c> permission) lives inside the service so it can
/// inspect the dossier's <c>AssignedExaminerId</c> column before deciding — see
/// <see cref="IApplicationProcessingContextService"/> for the contract.
/// </para>
/// <para>
/// <b>Rate limiting.</b> The controller participates in the standard authenticated
/// rate limit (<see cref="RateLimitingPolicies.Authenticated"/>). The dossier-load
/// surface is the natural target for scraping — the per-call audit row plus the
/// rate limit jointly contain the impact.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "cnas-admin,cnas-user")]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/applications")]
public sealed class ApplicationProcessingController : ControllerBase
{
    private readonly IApplicationProcessingContextService _svc;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the controller with its service collaborators.</summary>
    /// <param name="svc">Processing-context aggregator.</param>
    /// <param name="sqids">Sqid decoder for the route parameter.</param>
    public ApplicationProcessingController(
        IApplicationProcessingContextService svc,
        ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(sqids);
        _svc = svc;
        _sqids = sqids;
    }

    /// <summary>
    /// R0701 — returns the processing-context payload for the supplied application.
    /// </summary>
    /// <param name="applicationSqid">Sqid-encoded id of the target ServiceApplication.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// 200 OK with the populated <see cref="ApplicationProcessingContextDto"/> on
    /// success; 400 ProblemDetails when the Sqid is malformed; 401 when the caller
    /// is anonymous (defense-in-depth — the [Authorize] attribute fires first); 403
    /// when the caller lacks the processing permission AND is not the assigned
    /// examiner AND is not an admin; 404 when the application is missing.
    /// </returns>
    [HttpGet("{applicationSqid}/processing-context")]
    public async Task<ActionResult<ApplicationProcessingContextDto>> GetProcessingContextAsync(
        string applicationSqid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applicationSqid);

        var decoded = _sqids.TryDecode(applicationSqid);
        if (decoded.IsFailure)
        {
            return MapFailure(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc
            .GetForCurrentUserAsync(decoded.Value, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Maps a service-level <see cref="Result"/> failure to the appropriate
    /// ProblemDetails ActionResult: <see cref="ErrorCodes.NotFound"/> → 404,
    /// <see cref="ErrorCodes.Unauthorized"/> → 401,
    /// <see cref="ErrorCodes.Forbidden"/> → 403, everything else → 400.
    /// </summary>
    /// <param name="errorCode">Stable error code from the service.</param>
    /// <param name="errorMessage">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private static ObjectResult MapFailure(string? errorCode, string? errorMessage)
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
            Title = "Application processing context rejected.",
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
