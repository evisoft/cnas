namespace Cnas.Ps.Contracts;

/// <summary>
/// R2175 / R2134 — payload returned by <c>GET /api/health/database</c>
/// carrying the per-endpoint readiness state of the OLTP primary and the
/// OLAP read-replica.
/// </summary>
/// <param name="Primary">
/// Status of the primary (OLTP) endpoint. One of <c>"Healthy"</c> or
/// <c>"Unhealthy"</c>. When this is <c>"Unhealthy"</c> the HTTP response is
/// 503; otherwise 200.
/// </param>
/// <param name="Replica">
/// Status of the replica (OLAP) endpoint. One of <c>"Healthy"</c> or
/// <c>"Degraded"</c>. A <c>"Degraded"</c> replica does NOT escalate the
/// overall HTTP status — reporting transparently falls back to the primary —
/// but operators see the yellow indicator on the dashboard.
/// </param>
public sealed record DatabaseHealthStatusDto(string Primary, string Replica);
