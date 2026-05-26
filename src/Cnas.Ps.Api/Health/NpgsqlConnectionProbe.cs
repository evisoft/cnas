using Npgsql;

namespace Cnas.Ps.Api.Health;

/// <summary>
/// R2175 / R2134 — production <see cref="IDatabaseConnectionProbe"/> backed
/// by Npgsql. Opens a fresh connection, issues <c>SELECT 1</c>, and reports
/// reachability. Used by <see cref="DatabaseReplicaHealthCheck"/> to verify
/// the primary and replica endpoints.
/// </summary>
/// <remarks>
/// <para>
/// The probe deliberately opens a NEW connection per call rather than
/// renting from the application pool — pool exhaustion on the OLTP path
/// (PSR 003: 2000 concurrent users) must not block the readiness probe, and
/// the probe is what tells the load balancer whether the backend is
/// reachable in the first place. The open + SELECT round-trip is bounded
/// by <see cref="ProbeTimeout"/> so a stuck Postgres / PgBouncer cannot
/// indefinitely park the readiness sweep.
/// </para>
/// <para>
/// Every failure path (transport, auth, query, timeout) is collapsed into a
/// <c>false</c> return value — the health check uses the boolean to classify
/// the result and forwarding individual exception details would let internal
/// errors leak through <c>/health/database</c>. The unhandled-exception
/// middleware (SEC 057) is the right place to log the diagnostic, not the
/// probe.
/// </para>
/// </remarks>
public sealed class NpgsqlConnectionProbe : IDatabaseConnectionProbe
{
    /// <summary>
    /// Hard ceiling for the probe round-trip. Aligns with the MGov / MinIO
    /// readiness probes so a single slow dependency cannot stretch the
    /// overall <c>/health/ready</c> latency budget.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public async Task<bool> IsReachableAsync(string connectionString, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(ProbeTimeout);

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(probeCts.Token).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand("SELECT 1", connection);
            cmd.CommandTimeout = (int)ProbeTimeout.TotalSeconds;
            var result = await cmd.ExecuteScalarAsync(probeCts.Token).ConfigureAwait(false);
            return result is int i && i == 1;
        }
#pragma warning disable CA1031 // Probe MUST collapse every failure into false — the health-check contract is boolean.
        catch
        {
            return false;
        }
#pragma warning restore CA1031
    }
}
