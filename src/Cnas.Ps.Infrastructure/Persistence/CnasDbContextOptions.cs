namespace Cnas.Ps.Infrastructure.Persistence;

/// <summary>
/// R2175 / R2134 / R0026 — strongly-typed options that drive the
/// <see cref="DataPersistenceServiceCollectionExtensions"/>
/// <c>AddCnasDataPersistence</c> wiring. Bundles the primary (OLTP) and replica (OLAP) connection strings
/// together with the per-replica command timeout so the persistence layer can
/// be configured from a single section (<see cref="SectionName"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated options class.</b> The persistence wiring sits at the
/// crossroads of three TOR requirements:
/// <list type="bullet">
///   <item>TOR ARH 025 (R2134) — OLTP / OLAP separation. Writes and registry
///         listings stay on the primary; reporting aggregations move to the
///         replica.</item>
///   <item>TOR PSR 006 (R2175) — reports MUST hit the read-replica so a
///         long-running aggregation cannot crush the OLTP backend.</item>
///   <item>R0026 — the <c>IReadOnlyCnasDbContext</c> abstraction is the seam
///         that lets services opt into replica routing without leaking the
///         topology.</item>
/// </list>
/// Operators want ONE place to configure the topology; embedding the keys in
/// <c>InfrastructureServiceCollectionExtensions</c> made the contract
/// impossible to discover. <see cref="SectionName"/> is the single source of
/// truth and the dotted-key paths are what the Helm chart writes as pod env
/// vars.
/// </para>
/// <para>
/// <b>Fallback semantics.</b> When <see cref="ReplicaConnectionString"/> is
/// <c>null</c> or whitespace the DI wiring transparently routes the read-only
/// context to <see cref="PrimaryConnectionString"/> and emits a WARN log
/// line. This is acceptable for dev / single-Postgres staging deployments;
/// the WARN line is the signal that operators MUST address before
/// production. See <see cref="ReadReplicaConfiguration.ResolveConnectionString"/>.
/// </para>
/// <para>
/// <b>Timeout knob.</b>
/// <see cref="ReplicaCommandTimeoutSeconds"/> is wired onto the read-only
/// <c>DbContext.Database.CommandTimeout</c> so reporting queries cannot park
/// a worker indefinitely; this is the per-query budget the gate from
/// <see cref="PostgresPoolOptions.CommandTimeout"/> defaults to (30 s) but can
/// be lengthened for aggregation-heavy workloads via configuration without
/// touching the OLTP path.
/// </para>
/// </remarks>
public sealed class CnasDbContextOptions
{
    /// <summary>
    /// Configuration section name — <c>Cnas:Database</c>. The Helm chart
    /// writes env vars of the form <c>Cnas__Database__PrimaryConnectionString</c>,
    /// which the .NET configuration provider translates to this dotted key.
    /// </summary>
    public const string SectionName = "Cnas:Database";

    /// <summary>
    /// Required primary (OLTP) connection string. Routes every
    /// <c>ICnasDbContext</c> resolution. An empty / whitespace value is a
    /// startup failure — see
    /// <see cref="DataPersistenceServiceCollectionExtensions.AddCnasDataPersistence"/>.
    /// </summary>
    public string PrimaryConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Optional replica (OLAP) connection string. When <c>null</c> or
    /// whitespace the read-only context falls back to
    /// <see cref="PrimaryConnectionString"/> and a single WARN log line is
    /// emitted at startup. Identical-to-primary is treated the same as
    /// <c>null</c> by the health check (no double-charging the readiness
    /// probe for the same backend).
    /// </summary>
    public string? ReplicaConnectionString { get; set; }

    /// <summary>
    /// Per-call command timeout (seconds) applied to the read-only context.
    /// Defaults to 30, matching <see cref="PostgresPoolOptions.CommandTimeout"/>.
    /// Reporting workloads that genuinely need a longer ceiling raise this
    /// value via configuration without affecting the OLTP path.
    /// </summary>
    public int ReplicaCommandTimeoutSeconds { get; set; } = 30;
}
