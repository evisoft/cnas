namespace Cnas.Ps.Api.Health;

/// <summary>
/// R2175 / R2134 — abstraction over a low-cost "is the database reachable"
/// probe used by <see cref="DatabaseReplicaHealthCheck"/>. The default
/// production implementation is <see cref="NpgsqlConnectionProbe"/> which
/// opens an Npgsql connection and issues <c>SELECT 1</c>; tests substitute
/// an in-memory stub so the health-check contract can be exercised without
/// a live backend.
/// </summary>
/// <remarks>
/// <para>
/// The probe is deliberately stateless and stringly-typed — the connection
/// string is passed per call rather than captured at construction so the
/// same probe instance can interrogate both the primary and replica
/// endpoints. The health check runs at most once per readiness sweep so the
/// connection-open cost (TLS + SCRAM) is acceptable; PgBouncer in front of
/// Postgres absorbs the open burst.
/// </para>
/// <para>
/// Implementations MUST return <c>false</c> on any transport / authentication
/// / query failure rather than propagating the exception — the health check
/// uses the boolean to classify Healthy / Degraded / Unhealthy and the
/// stack trace is uninteresting at the probe boundary.
/// </para>
/// </remarks>
public interface IDatabaseConnectionProbe
{
    /// <summary>
    /// Probes <paramref name="connectionString"/> with a cheap reachability
    /// check (e.g. <c>SELECT 1</c>). Returns <c>true</c> when the endpoint
    /// accepts a connection and the round-trip query returns the expected
    /// scalar; <c>false</c> on any error.
    /// </summary>
    /// <param name="connectionString">
    /// Connection string to probe. Treated as opaque — the probe doesn't
    /// inspect the contents beyond passing it to the provider client.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the probe — health-check frameworks usually pass a deadline
    /// token so a stuck connection cannot park the readiness sweep.
    /// </param>
    /// <returns><c>true</c> when reachable; <c>false</c> on any failure.</returns>
    Task<bool> IsReachableAsync(string connectionString, CancellationToken cancellationToken);
}
