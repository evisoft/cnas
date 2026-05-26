namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0183 / SEC 043 — tunable parameters for the per-entity field-policy resolver
/// and the companion background cache-refresh job. Bound from the
/// <c>Cnas:AuditFieldPolicy</c> configuration section so operators can adjust the
/// refresh cadence without redeploying.
/// </summary>
public sealed class AuditFieldPolicyOptions
{
    /// <summary>Configuration section name used by the host bindings.</summary>
    public const string SectionName = "Cnas:AuditFieldPolicy";

    /// <summary>
    /// Refresh cadence in seconds for the in-memory policy snapshot. The cache is
    /// also invalidated synchronously on every CRUD mutation, so this background
    /// cadence is the ceiling on staleness. Defaults to 60 seconds (mirrors the
    /// R0182 audit-policy refresh cadence).
    /// </summary>
    public int RefreshIntervalSeconds { get; init; } = 60;
}
