namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0126 / CF 16.10 — tunable parameters for the workflow ACL resolver and its
/// background cache refresh job. Bound from the <c>Cnas:WorkflowAcl</c> configuration
/// section so operators can adjust the refresh cadence without redeploying.
/// </summary>
public sealed class WorkflowAclOptions
{
    /// <summary>Configuration section name used by the host bindings.</summary>
    public const string SectionName = "Cnas:WorkflowAcl";

    /// <summary>
    /// Refresh cadence in seconds for the in-memory ACL snapshot. The cache is also
    /// invalidated synchronously on every CRUD mutation, so this background cadence
    /// is the "ceiling" on how stale the snapshot can be — usually it is already
    /// current. Defaults to 60 seconds (matches the AuditPolicyResolver cadence).
    /// </summary>
    public int RefreshIntervalSeconds { get; init; } = 60;
}
