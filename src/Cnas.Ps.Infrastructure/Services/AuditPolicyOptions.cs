namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0182 / SEC 042 — tunable parameters for the audit-policy resolver and the
/// background cache refresh job. Bound from the <c>Cnas:AuditPolicy</c> configuration
/// section so operators can adjust the refresh cadence without redeploying.
/// </summary>
public sealed class AuditPolicyOptions
{
    /// <summary>Configuration section name used by the host bindings.</summary>
    public const string SectionName = "Cnas:AuditPolicy";

    /// <summary>
    /// Refresh cadence in seconds for the in-memory policy snapshot. The cache is
    /// also invalidated synchronously on every CRUD mutation, so this background
    /// cadence is the "ceiling" on how stale the snapshot can be — usually it is
    /// already current. Defaults to 60 seconds.
    /// </summary>
    public int RefreshIntervalSeconds { get; init; } = 60;
}
