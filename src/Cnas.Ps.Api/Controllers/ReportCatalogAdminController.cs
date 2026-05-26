using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R1900-R1905 / TOR §13 Annex 6 — admin REST surface over the persisted
/// report catalog. Restricted to the
/// <see cref="AuthorizationComposition.CnasAdmin"/> policy because the
/// refresh path mutates the catalog table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>GET  /api/admin/reports/catalog</c> — list the persisted catalog (optional category/frequency filter).</item>
///   <item><c>POST /api/admin/reports/catalog/refresh</c> — re-seed / upsert from the in-code descriptor table.</item>
/// </list>
/// </para>
/// <para>
/// <b>Idempotence.</b> The refresh endpoint is safe to call repeatedly; the
/// returned envelope reports inserts vs upserts vs unchanged so the operator
/// can confirm the catalog converged.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/reports/catalog")]
public sealed class ReportCatalogAdminController : ControllerBase
{
    private readonly IReportCatalogSeedService _service;

    /// <summary>Constructs the controller.</summary>
    /// <param name="service">Report-catalog seed façade.</param>
    public ReportCatalogAdminController(IReportCatalogSeedService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <summary>
    /// Returns the persisted catalog rows. Optional <paramref name="category"/>
    /// / <paramref name="frequency"/> filters use exact-string match.
    /// </summary>
    /// <param name="category">Optional category filter (e.g. <c>AuditSecurity</c>).</param>
    /// <param name="frequency">Optional frequency filter (e.g. <c>Monthly</c>).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the catalog page; 500 on DB error.</returns>
    [HttpGet("")]
    public async Task<ActionResult<ReportCatalogPageDto>> ListAsync(
        [FromQuery] string? category = null,
        [FromQuery] string? frequency = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ListAsync(category, frequency, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ReportCatalogPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Re-seeds / refreshes the persisted catalog from
    /// <c>ReportCatalogDescriptors</c>. Emits a Critical audit row on success.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the refresh outcome; 500 on DB error.</returns>
    [HttpPost("refresh")]
    public async Task<ActionResult<ReportCatalogRefreshResultDto>> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _service.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ReportCatalogRefreshResultDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Maps a failed <see cref="Result{T}"/> to the appropriate HTTP status.
    /// </summary>
    /// <typeparam name="T">DTO type that would have been returned on success.</typeparam>
    /// <param name="errorCode">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="errorMessage">Human-readable description.</param>
    /// <returns>An <see cref="ActionResult{T}"/> carrying the appropriate HTTP status.</returns>
    private ActionResult<T> MapFailure<T>(string errorCode, string errorMessage)
        => errorCode switch
        {
            ErrorCodes.InvalidSqid => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.ValidationFailed => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
