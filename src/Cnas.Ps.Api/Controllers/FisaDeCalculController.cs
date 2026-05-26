using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0932 / TOR §10.1 — REST surface for the Fișa de calcul interactive recalc
/// endpoint. POST /api/decisions/fisa-de-calcul/recalc accepts the operator-
/// edited rows and returns the refreshed total. Gated by
/// <see cref="AuthorizationComposition.CnasUser"/> — the recalc is part of UC08
/// (examination).
/// </summary>
/// <param name="recalculator">Stateless recalculator service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasUser)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/decisions/fisa-de-calcul")]
public sealed class FisaDeCalculController(IFisaDeCalculRecalculator recalculator) : ControllerBase
{
    private readonly IFisaDeCalculRecalculator _recalculator = recalculator;

    /// <summary>
    /// R0932 — re-runs the formula evaluator against the supplied edited rows
    /// and returns the refreshed total. Pure preview — does not persist the
    /// recomputed Fișa back to the dossier.
    /// </summary>
    /// <param name="input">Operator-edited row set.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 OK with the result body; 400 ProblemDetails on validation failure.</returns>
    [HttpPost("recalc")]
    public async Task<ActionResult<FisaDeCalculRecalcResultDto>> RecalcAsync(
        [FromBody] FisaDeCalculRecalcInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _recalculator.RecalculateAsync(input, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return BadRequest(new ProblemDetails
            {
                Title = result.ErrorCode,
                Detail = result.ErrorMessage,
                Status = StatusCodes.Status400BadRequest,
            });
        }
        return Ok(result.Value);
    }
}
