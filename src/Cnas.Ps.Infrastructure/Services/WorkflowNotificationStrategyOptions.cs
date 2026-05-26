namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0128 / R0173 — tunable parameters for the per-workflow notification-strategy
/// resolver and the background cache refresh job. Bound from
/// <c>Cnas:WorkflowNotificationStrategy</c> so operators can adjust the cadence
/// without redeploying.
/// </summary>
public sealed class WorkflowNotificationStrategyOptions
{
    /// <summary>Configuration section name used by the host bindings.</summary>
    public const string SectionName = "Cnas:WorkflowNotificationStrategy";

    /// <summary>
    /// Refresh cadence in seconds for the in-memory strategy snapshot. The cache is
    /// also invalidated synchronously on every CRUD mutation, so this background
    /// cadence is the ceiling on staleness — usually the snapshot is already current.
    /// Defaults to 60 seconds, matching the R0182 audit-policy resolver cadence.
    /// </summary>
    public int RefreshIntervalSeconds { get; init; } = 60;
}
