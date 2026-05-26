using Cnas.Ps.Api.Health;
using Cnas.Ps.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2175 / R2134 — REST surface over <see cref="DatabaseReplicaHealthCheck"/>.
/// Exposes <c>GET /api/health/database</c> so dashboards can render the
/// OLTP / OLAP split health indicator without scraping the generic
/// <c>/health/ready</c> payload and filtering for the database row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated controller.</b> The <c>/health/ready</c> endpoint
/// returns the full readiness sweep including MGov dependencies; a UI that
/// only cares about the database state had to parse the full payload. The
/// dedicated controller returns a stable two-field DTO
/// (<see cref="DatabaseHealthStatusDto"/>) so the wire contract is
/// trivially consumable.
/// </para>
/// <para>
/// <b>Authorization.</b> The endpoint is anonymous so external monitoring
/// systems (Pingdom, k8s probes, internal Grafana) can scrape it without an
/// auth token. The body carries only the status string per endpoint — no
/// connection details, no exception payloads, no PII — so leaking the
/// surface is acceptable. The <see cref="AllowAnonymousAttribute"/> is
/// explicit to keep the security review (SEC 058) ergonomic.
/// </para>
/// <para>
/// <b>HTTP status mapping.</b>
/// Primary unreachable → 503 (<see cref="StatusCodes.Status503ServiceUnavailable"/>).
/// Anything else → 200. The replica being degraded does NOT escalate to
/// 503 — the system is still serving reads (with a fallback to the primary).
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("api/health/database")]
public sealed class HealthDatabaseController : ControllerBase
{
    private readonly DatabaseReplicaHealthCheck _check;

    /// <summary>
    /// Constructs the controller with an injected
    /// <see cref="DatabaseReplicaHealthCheck"/>. The check is registered by
    /// the composition root as a singleton (it holds the two connection
    /// strings + the probe; no per-request state).
    /// </summary>
    /// <param name="check">Health check used to compute the per-endpoint states.</param>
    public HealthDatabaseController(DatabaseReplicaHealthCheck check)
    {
        _check = check ?? throw new ArgumentNullException(nameof(check));
    }

    /// <summary>
    /// Returns the current per-endpoint readiness state of the OLTP primary
    /// and the OLAP read-replica.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with <see cref="DatabaseHealthStatusDto"/> when the primary is
    /// reachable (even if the replica is degraded). 503 with the same body
    /// shape when the primary is unreachable.
    /// </returns>
    [HttpGet]
    [ProducesResponseType(typeof(DatabaseHealthStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(DatabaseHealthStatusDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DatabaseHealthStatusDto>> GetAsync(CancellationToken cancellationToken)
    {
        var result = await _check.CheckHealthAsync(new HealthCheckContext(), cancellationToken)
            .ConfigureAwait(false);

        var primary = ResolveStatus(result, DatabaseReplicaHealthCheck.PrimaryDataKey);
        var replica = ResolveStatus(result, DatabaseReplicaHealthCheck.ReplicaDataKey);
        var dto = new DatabaseHealthStatusDto(primary, replica);

        // Primary unhealthy → 503; replica degraded does NOT escalate because the
        // backend is still serving reads (with fallback to the primary). See the
        // class-level remarks for the classification table.
        if (result.Status == HealthStatus.Unhealthy)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, dto);
        }

        return Ok(dto);
    }

    /// <summary>
    /// Pulls a stringly-typed status out of the
    /// <see cref="HealthCheckResult.Data"/> bag, defaulting to "Unknown" if
    /// the key is missing — the health check always populates both keys, so
    /// the default is only a defensive fallback.
    /// </summary>
    private static string ResolveStatus(HealthCheckResult result, string key)
    {
        if (result.Data.TryGetValue(key, out var value) && value is string str)
        {
            return str;
        }
        return "Unknown";
    }
}
