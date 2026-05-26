using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Archive;
using Cnas.Ps.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0332 / TOR CF 12.02 — electronic-archive metadata REST surface. Exposes a
/// single <c>GET /api/archive/summary</c> endpoint that returns the five-tab
/// header strip the Web archive page renders above each register tab.
/// Authenticated CNAS-staff role required; the payload is depersonalised
/// (counts only) so it does not need additional row-level scoping.
/// </summary>
/// <param name="metadata">Summariser façade.</param>
[ApiController]
[Authorize(Roles = "cnas-user,cnas-admin")]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/archive")]
public sealed class ArchiveSummaryController(IArchiveMetadataService metadata) : ControllerBase
{
    private readonly IArchiveMetadataService _metadata = metadata;

    /// <summary>
    /// Returns the per-tab counts (active + archived) and the
    /// last-updated-utc badge for each register surfaced by the
    /// <c>/archive</c> tabbed UI.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the populated summary, 500 on underlying I/O failure.</returns>
    [HttpGet("summary")]
    public async Task<ActionResult<ArchiveSummaryDto>> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var result = await _metadata.GetSummaryAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return Problem(result.ErrorMessage, statusCode: StatusCodes.Status500InternalServerError);
    }
}
