using Cnas.Ps.Api.Composition;
using Cnas.Ps.Contracts;
using Cnas.Ps.Application.UseCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>UC04 — Dashboard widgets.</summary>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/dashboard")]
public sealed class DashboardController(IDashboardService dashboard) : ControllerBase
{
    /// <summary>Returns the KPI widgets for the calling user.</summary>
    [HttpGet("widgets")]
    public async Task<ActionResult<IReadOnlyList<KpiWidget>>> WidgetsAsync(CancellationToken cancellationToken = default)
    {
        var result = await dashboard.GetWidgetsAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : Problem(result.ErrorMessage, statusCode: 500);
    }

    /// <summary>
    /// R0533 / TOR CF 04.04 — returns the combined dashboard snapshot for the calling
    /// user: the legacy per-category widget list AND the aggregate KPI grid cells.
    /// Deep-link URLs populated server-side per R0534 so the UI renders them as
    /// click-through anchors.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the snapshot; 500 ProblemDetails on infrastructure failure.</returns>
    [HttpGet("snapshot")]
    public async Task<ActionResult<DashboardSnapshotDto>> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var result = await dashboard.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : Problem(result.ErrorMessage, statusCode: 500);
    }
}
