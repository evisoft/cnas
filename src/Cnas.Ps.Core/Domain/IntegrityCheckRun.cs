namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2282 / TOR SEC 036 — one execution of the data-integrity sweep. Each row
/// captures the start / end timestamps, the trigger origin, the status, and
/// the aggregated finding counts so operators can chart per-run health from
/// the dashboard without scanning the per-finding table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> The job creates a <c>Running</c> row at the start of the
/// sweep with <c>RunCompletedAt = null</c>. On success the job flips the row
/// to <c>Completed</c> and stamps the completion timestamp plus the aggregated
/// totals. On an unhandled exception the job marks the row <c>Failed</c>
/// and populates <see cref="FailureReason"/> for the operator post-mortem.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// outbound DTO (<c>Cnas.Ps.Contracts.IntegrityCheckRunDto.Id</c>) carries a
/// Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// <para>
/// <b>No PII.</b> The run row stores aggregated counters only — no IDNP /
/// IDNO / IBAN / personal-name text is ever persisted here. The companion
/// <see cref="IntegrityCheckFinding"/> rows are likewise PII-free; sensitive
/// references go through the raw <c>AggregateRowId</c> column which is
/// resolvable only by operators with direct DB access.
/// </para>
/// </remarks>
public sealed class IntegrityCheckRun : AuditableEntity, IExternalId
{
    /// <summary>UTC timestamp the run started (job fire / manual trigger).</summary>
    public DateTime RunStartedAt { get; set; }

    /// <summary>
    /// UTC timestamp the run completed (either Completed or Failed). Null while
    /// the run is still in <see cref="IntegrityCheckRunStatus.Running"/>.
    /// </summary>
    public DateTime? RunCompletedAt { get; set; }

    /// <summary>Whether the run was fired by the scheduler or a manual operator action.</summary>
    public IntegrityCheckTriggerKind TriggerKind { get; set; }

    /// <summary>Lifecycle status — defaults to <see cref="IntegrityCheckRunStatus.Running"/>.</summary>
    public IntegrityCheckRunStatus Status { get; set; } = IntegrityCheckRunStatus.Running;

    /// <summary>Total rows the run scanned across every check.</summary>
    public long TotalRowsScanned { get; set; }

    /// <summary>Total findings the run recorded.</summary>
    public int TotalFindings { get; set; }

    /// <summary>
    /// JSON-serialised dictionary of severity-name → finding-count
    /// (e.g. <c>{"Critical": 2, "High": 5, "Medium": 0, "Low": 1}</c>).
    /// Null while the run is in flight; populated atomically at completion.
    /// </summary>
    public string? FindingsBySeverity { get; set; }

    /// <summary>
    /// Operator-facing reason populated when <see cref="Status"/> is
    /// <see cref="IntegrityCheckRunStatus.Failed"/>. Null otherwise.
    /// </summary>
    public string? FailureReason { get; set; }
}
