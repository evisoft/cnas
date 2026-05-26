namespace Cnas.Ps.Infrastructure.Persistence;

/// <summary>
/// R0025 — per-pod Npgsql connection-pool sizing in front of PgBouncer.
/// Bound from the <c>Postgres:Pool:*</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// The CNAS „Protecția Socială" SLO (TOR PSR 003) is <b>2000 concurrent users</b>.
/// Each in-flight web request can hold a database connection for its duration, so
/// at peak we expect ~2000 active client connections per pod. PostgreSQL cannot
/// sustain that many native backends (each one is a process consuming ~10 MB RAM
/// + 1 PID), so production fronts Postgres with <b>PgBouncer in transaction-pooling
/// mode</b> which multiplexes those onto ~50 actual Postgres backends.
/// </para>
/// <para>
/// The two-tier sizing:
/// <list type="bullet">
///   <item>App-side: Npgsql <see cref="MaxPoolSize"/> = 2000 per pod (matches the SLO).</item>
///   <item>PgBouncer-side: <c>default_pool_size</c> = 50 (server-side multiplex).</item>
///   <item>PgBouncer-side: <c>max_client_conn</c> = 2500 (accept budget, slightly above
///         app cap to absorb burst).</item>
/// </list>
/// See <c>docs/operations.md</c> §"Database connection pooling (R0025)" for the
/// production topology and operator knobs.
/// </para>
/// <para>
/// When <see cref="UsePgBouncer"/> is <c>true</c> (the default) the
/// <c>AddCnasInfrastructure</c> wiring also flips three Npgsql settings:
/// <list type="bullet">
///   <item><c>Max Auto Prepare = 0</c> — transaction-pooled PgBouncer cannot keep
///         session-state prepared statements between checkouts.</item>
///   <item><c>No Reset On Close = true</c> — the server-reset is performed by
///         PgBouncer via <c>SERVER_RESET_QUERY</c> instead of Npgsql.</item>
///   <item><c>Server Compatibility Mode = NoTypeLoading</c> — skips the pg_catalog
///         type-loading round-trip that PgBouncer cannot proxy reliably in
///         transaction mode.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class PostgresPoolOptions
{
    /// <summary>Configuration section name — <c>Postgres:Pool</c>.</summary>
    public const string SectionName = "Postgres:Pool";

    /// <summary>
    /// Per-pod maximum Npgsql connection-pool size. Defaults to 2000 to match the
    /// TOR PSR 003 SLO (2000 concurrent users → up to 2000 in-flight DB connections
    /// per pod). Operators override per environment via <c>Postgres:Pool:MaxPoolSize</c>.
    /// </summary>
    public int MaxPoolSize { get; set; } = 2000;

    /// <summary>
    /// Minimum number of idle connections the pool keeps alive. Defaults to 5 so a
    /// cold pod has a handful of connections ready immediately when the first request
    /// arrives without paying the TLS / SCRAM handshake cost on the critical path.
    /// </summary>
    public int MinPoolSize { get; set; } = 5;

    /// <summary>
    /// Seconds an idle connection sits in the pool before becoming eligible for
    /// pruning. Defaults to 300 (5 minutes) — long enough to absorb most diurnal
    /// traffic dips, short enough that an outage doesn't leave stale TCP sockets
    /// pinned to a dead PgBouncer pod.
    /// </summary>
    public int ConnectionIdleLifetime { get; set; } = 300;

    /// <summary>
    /// Seconds between pool-pruner sweeps. Defaults to 10. The pruner is what
    /// actually closes connections older than <see cref="ConnectionIdleLifetime"/>;
    /// running it more often reclaims sockets faster but burns a touch more CPU on
    /// the timer thread.
    /// </summary>
    public int ConnectionPruningInterval { get; set; } = 10;

    /// <summary>
    /// Per-call command timeout in seconds. Defaults to 30 — the same upper bound as
    /// every outbound MGov client, so a single misbehaving DB call cannot stretch a
    /// request past its end-to-end budget. Reporting queries that genuinely need
    /// longer should be moved to the background-job lane (R0048 / Quartz) rather
    /// than raising this default.
    /// </summary>
    public int CommandTimeout { get; set; } = 30;

    /// <summary>
    /// <c>true</c> when the upstream is PgBouncer in transaction-pooling mode
    /// (the production topology). When <c>true</c>, <c>AddCnasInfrastructure</c>
    /// disables prepared-statement auto-preparation, disables Npgsql's server-state
    /// reset on close, and sets <c>Server Compatibility Mode = NoTypeLoading</c> —
    /// see the type-level remarks for the full rationale.
    /// </summary>
    /// <remarks>
    /// Set to <c>false</c> for local debugging directly against PostgreSQL (e.g.
    /// when bypassing PgBouncer in the dev compose stack). Production / staging MUST
    /// leave this at <c>true</c>.
    /// </remarks>
    public bool UsePgBouncer { get; set; } = true;
}
