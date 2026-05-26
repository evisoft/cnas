using System.Collections.Generic;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cnas.Ps.Api.Health;

/// <summary>
/// R2175 / R2134 — readiness probe that distinguishes "primary up" from
/// "replica up" so dashboards can render the OLTP / OLAP split health
/// indicator and the load balancer can drain the pod only when the primary
/// is actually offline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Classification table.</b>
/// <list type="bullet">
///   <item><see cref="HealthStatus.Healthy"/> — both endpoints reachable.</item>
///   <item><see cref="HealthStatus.Degraded"/> — primary reachable, replica
///         unreachable. Reporting transparently falls back to the primary
///         (the same fallback the DI wiring uses when no replica is
///         configured); the alert is informational, not pager-worthy.</item>
///   <item><see cref="HealthStatus.Unhealthy"/> — primary unreachable. The
///         backend is offline and the load balancer must drain the pod even
///         if the replica is still serving reads.</item>
/// </list>
/// </para>
/// <para>
/// <b>Single-Postgres topology.</b> When the replica connection string is
/// null / whitespace / identical to the primary the check probes the primary
/// once and mirrors the result onto the replica state — dev and single-node
/// staging deployments don't double-charge the readiness budget for the
/// same backend.
/// </para>
/// <para>
/// <b>Data payload.</b>
/// <see cref="HealthCheckResult.Data"/> carries the per-endpoint string
/// status (<c>"Healthy"</c> / <c>"Degraded"</c> / <c>"Unhealthy"</c>) so
/// <see cref="Cnas.Ps.Api.Controllers.HealthDatabaseController"/> can render
/// the JSON payload without re-probing.
/// </para>
/// </remarks>
public sealed class DatabaseReplicaHealthCheck : IHealthCheck
{
    /// <summary>Per-endpoint status emitted in the <see cref="HealthCheckResult.Data"/> bag.</summary>
    private const string StatusHealthy = "Healthy";

    /// <summary>Per-endpoint status emitted when the replica is offline but the primary is up.</summary>
    private const string StatusDegraded = "Degraded";

    /// <summary>Per-endpoint status emitted when the primary is offline.</summary>
    private const string StatusUnhealthy = "Unhealthy";

    /// <summary>Data-bag key for the primary endpoint state.</summary>
    public const string PrimaryDataKey = "primary";

    /// <summary>Data-bag key for the replica endpoint state.</summary>
    public const string ReplicaDataKey = "replica";

    private readonly IDatabaseConnectionProbe _probe;
    private readonly string _primaryConnectionString;
    private readonly string? _replicaConnectionString;

    /// <summary>
    /// Constructs the health check with the two connection strings it will
    /// probe. The primary is required; the replica is optional and may be
    /// <c>null</c> / identical to the primary (single-Postgres topology).
    /// </summary>
    /// <param name="probe">Connection probe — production uses <see cref="NpgsqlConnectionProbe"/>.</param>
    /// <param name="primaryConnectionString">Primary (OLTP) connection string.</param>
    /// <param name="replicaConnectionString">
    /// Replica (OLAP) connection string. When <c>null</c> / whitespace /
    /// identical to <paramref name="primaryConnectionString"/> the probe runs
    /// once and the replica state mirrors the primary state.
    /// </param>
    public DatabaseReplicaHealthCheck(
        IDatabaseConnectionProbe probe,
        string primaryConnectionString,
        string? replicaConnectionString)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _primaryConnectionString = primaryConnectionString
            ?? throw new ArgumentNullException(nameof(primaryConnectionString));
        _replicaConnectionString = replicaConnectionString;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var primaryUp = await _probe.IsReachableAsync(_primaryConnectionString, cancellationToken)
            .ConfigureAwait(false);

        // Single-Postgres topology: replica connection string null / whitespace / equal to primary →
        // do not double-probe; mirror the primary result.
        bool replicaUp;
        if (string.IsNullOrWhiteSpace(_replicaConnectionString)
            || string.Equals(_replicaConnectionString, _primaryConnectionString, StringComparison.Ordinal))
        {
            replicaUp = primaryUp;
        }
        else
        {
            replicaUp = await _probe.IsReachableAsync(_replicaConnectionString, cancellationToken)
                .ConfigureAwait(false);
        }

        var data = new Dictionary<string, object>(2, StringComparer.Ordinal)
        {
            [PrimaryDataKey] = primaryUp ? StatusHealthy : StatusUnhealthy,
            [ReplicaDataKey] = replicaUp ? StatusHealthy : StatusDegraded,
        };

        if (!primaryUp)
        {
            return HealthCheckResult.Unhealthy(
                description: "Primary database endpoint is unreachable; pod must drain.",
                data: data);
        }

        if (!replicaUp)
        {
            return HealthCheckResult.Degraded(
                description: "Replica database endpoint is unreachable; reporting falls back to the primary.",
                data: data);
        }

        return HealthCheckResult.Healthy(
            description: "Primary and replica database endpoints are reachable.",
            data: data);
    }
}
