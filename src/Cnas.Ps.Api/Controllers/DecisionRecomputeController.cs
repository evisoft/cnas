using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R1502 / TOR §3.7-C — REST surface backing the recompute-on-base-change
/// pipeline. Single endpoint:
/// <c>POST /api/decisions/{sqid}/recompute</c>.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasDecider)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/decisions")]
public sealed class DecisionRecomputeController : ControllerBase
{
    private readonly IDecisionRecomputeService _svc;

    /// <summary>Constructs the controller with the underlying service.</summary>
    /// <param name="svc">Per-request decision-recompute service.</param>
    public DecisionRecomputeController(IDecisionRecomputeService svc)
    {
        ArgumentNullException.ThrowIfNull(svc);
        _svc = svc;
    }

    /// <summary>R1502 — recompute the supplied decision against a fresh base amount.</summary>
    /// <param name="sqid">Sqid-encoded id of the prior decision.</param>
    /// <param name="input">Recompute input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the outcome on success; 400/404 on failure.</returns>
    [HttpPost("{sqid}/recompute")]
    public async Task<ActionResult<DecisionRecomputeOutcomeDto>> RecomputeAsync(
        [FromRoute] string sqid,
        [FromBody] DecisionRecomputeInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var result = await _svc.RecomputeAsync(
            sqid,
            input.Reason,
            input.NewMonthlyAmountMdl,
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a service-layer failure to a typed <see cref="ActionResult{TValue}"/>.</summary>
    /// <param name="code">Stable error code from the service.</param>
    /// <param name="message">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ActionResult<DecisionRecomputeOutcomeDto> MapFailure(string? code, string? message)
    {
        var status = code switch
        {
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            ErrorCodes.Conflict => StatusCodes.Status409Conflict,
            ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest,
        };
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }
}

/// <summary>R1502 — input envelope for the recompute endpoint.</summary>
/// <param name="Reason">Stable reason for the recompute.</param>
/// <param name="NewMonthlyAmountMdl">Newly-computed monthly amount in MDL (non-negative).</param>
public sealed record DecisionRecomputeInputDto(
    DecisionRecomputeReason Reason,
    decimal NewMonthlyAmountMdl);
