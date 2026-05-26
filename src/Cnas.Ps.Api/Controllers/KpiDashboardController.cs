using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Kpi;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0201 / TOR CF 20.02 — read surface over the pre-aggregated KPI snapshot
/// store + admin run trigger. The GETs are gated by
/// <see cref="AuthorizationComposition.CnasUser"/> (every authenticated CNAS
/// operator can render the dashboard) and the run POST is gated by the
/// stricter <see cref="AuthorizationComposition.CnasTechAdmin"/> policy
/// (manual recompute is a maintenance action, not a routine one).
/// </summary>
/// <remarks>
/// <para>
/// Route table:
/// <list type="bullet">
///   <item><c>GET  /api/kpi/snapshots?fromDate=&amp;toDate=&amp;kpiCode=</c> — list snapshots.</item>
///   <item><c>GET  /api/kpi/latest?codes=Code1,Code2,...</c> — latest value per code.</item>
///   <item><c>POST /api/kpi/snapshots/run?date=YYYY-MM-DD</c> — admin-only recompute.</item>
/// </list>
/// </para>
/// </remarks>
/// <param name="svc">Underlying snapshot orchestrator + read facade.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasUser)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/kpi")]
public sealed class KpiDashboardController(IKpiSnapshotService svc) : ControllerBase
{
    private readonly IKpiSnapshotService _svc = svc;

    /// <summary>
    /// Returns every snapshot row whose
    /// <see cref="Cnas.Ps.Core.Domain.KpiSnapshot.SnapshotDate"/> falls in
    /// the inclusive range [<paramref name="fromDate"/>, <paramref name="toDate"/>],
    /// optionally filtered to a single KPI code. Sorted
    /// (<c>SnapshotDate DESC, KpiCode ASC, Dimension1 ASC, Dimension2 ASC</c>).
    /// </summary>
    /// <param name="fromDate">Inclusive lower-bound snapshot date.</param>
    /// <param name="toDate">Inclusive upper-bound snapshot date.</param>
    /// <param name="kpiCode">Optional KPI-code filter; null/empty returns every code.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the list of snapshot DTOs.</returns>
    [HttpGet("snapshots")]
    public async Task<ActionResult<IReadOnlyList<KpiSnapshotDto>>> ListSnapshotsAsync(
        [FromQuery] DateOnly fromDate,
        [FromQuery] DateOnly toDate,
        [FromQuery] string? kpiCode,
        CancellationToken cancellationToken = default)
    {
        var rows = await _svc.QueryAsync(fromDate, toDate, kpiCode, cancellationToken)
            .ConfigureAwait(false);
        return Ok(rows);
    }

    /// <summary>
    /// Returns the most-recent value per requested KPI code. The query
    /// parameter <paramref name="codes"/> is a comma-separated list of stable
    /// KPI codes; whitespace is trimmed; duplicates are deduplicated by the
    /// service.
    /// </summary>
    /// <param name="codes">Comma-separated list of KPI codes.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with the dictionary. KPI codes the store has never seen are
    /// omitted from the result rather than returning zero.
    /// </returns>
    [HttpGet("latest")]
    public async Task<ActionResult<IReadOnlyDictionary<string, decimal>>> GetLatestAsync(
        [FromQuery] string? codes,
        CancellationToken cancellationToken = default)
    {
        var split = (codes ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var data = await _svc.GetLatestAsync(split, cancellationToken).ConfigureAwait(false);
        return Ok(data);
    }

    /// <summary>
    /// Admin-only forced re-compute of the KPI snapshot for a specific date.
    /// Idempotent — the service upserts on the natural key, so a re-run
    /// overwrites the previous values in place. Useful for back-filling a
    /// missed day or after seeding fresh demo data.
    /// </summary>
    /// <param name="date">UTC calendar date to recompute.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the <see cref="KpiSnapshotRunDto"/> on success; ProblemDetails on failure.</returns>
    [HttpPost("snapshots/run")]
    [Authorize(Policy = AuthorizationComposition.CnasTechAdmin)]
    public async Task<ActionResult<KpiSnapshotRunDto>> RunAsync(
        [FromQuery] DateOnly date,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.RunForDateAsync(date, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Problem(result.ErrorMessage, statusCode: StatusForCode(result.ErrorCode));
        }
        return Ok(result.Value);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>The mapped HTTP status code.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
