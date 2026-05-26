namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0203 / TOR CF 20.06 — one row per external-source ingestion attempt. Mirrors
/// the pattern of <see cref="TreasuryFeedImport"/> but generalises across the
/// register-of-population (RSP), register of legal entities (RSUD), tax service
/// (SFS) and any future external data source. The nightly per-source Quartz
/// jobs insert a row, advance its <see cref="Status"/> through the lifecycle,
/// and finalise it with the per-source counters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b>
/// <see cref="ExternalSourceIngestionStatus.Pending"/> →
/// <see cref="ExternalSourceIngestionStatus.Running"/> →
/// <see cref="ExternalSourceIngestionStatus.Completed"/>. A failure at any
/// stage flips the row to <see cref="ExternalSourceIngestionStatus.Failed"/>
/// with a sanitised <see cref="FailureReason"/>; the scheduler also has the
/// option to record a <see cref="ExternalSourceIngestionStatus.Skipped"/> row
/// when an upstream gate refuses the run (e.g. peak-hour gate).
/// </para>
/// <para>
/// <b>Natural-key uniqueness.</b> A unique index on <see cref="RunNumber"/>
/// guarantees the per-year monotonic counter is stable. The
/// <c>(SourceCode, StartedAtUtc DESC)</c> index backs the admin list page.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because operators
/// reference an individual run by Sqid through the admin surface.
/// </para>
/// <para>
/// <b>No PII.</b> The row carries only counters, an opaque upstream pull-id,
/// and a sanitised failure reason — no IDNPs / addresses / names leak into
/// this aggregate.
/// </para>
/// </remarks>
public sealed class ExternalSourceIngestionRun : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable upper-case source-system code (e.g. <c>RSP</c>, <c>RSUD</c>,
    /// <c>SFS</c>). Constrained by the input validator's regex
    /// <c>^[A-Z][A-Z0-9_.]{1,63}$</c>.
    /// </summary>
    public required string SourceCode { get; set; }

    /// <summary>
    /// Human-friendly business-key carrying the per-year sequence number.
    /// Format <c>ESI-{year}-{seq:000000}</c>; unique across the table. Operators
    /// surface this on the admin list as the primary natural identifier.
    /// </summary>
    public required string RunNumber { get; set; }

    /// <summary>Current lifecycle status; defaults to <see cref="ExternalSourceIngestionStatus.Pending"/>.</summary>
    public ExternalSourceIngestionStatus Status { get; set; } = ExternalSourceIngestionStatus.Pending;

    /// <summary>Origin of the run (Scheduled vs Manual).</summary>
    public ExternalSourceTriggerKind TriggerKind { get; set; }

    /// <summary>UTC instant the ingestion started.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>UTC instant the run transitioned to a terminal state. Null while in flight.</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Total records the connector pulled from the upstream source.</summary>
    public long TotalRecordsPulled { get; set; }

    /// <summary>Total records successfully applied to local stores.</summary>
    public long TotalRecordsApplied { get; set; }

    /// <summary>Total records skipped (idempotent / unchanged content).</summary>
    public long TotalRecordsSkipped { get; set; }

    /// <summary>Total records that failed during apply.</summary>
    public long TotalRecordsFailed { get; set; }

    /// <summary>
    /// Sanitised, PII-free failure reason. Bounded to ≤ 1000 characters by
    /// the EF configuration. Null on success.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Opaque upstream pull-identifier returned by the connector (e.g. an MConnect
    /// transaction id). Surfaces on the admin row so operators can correlate a
    /// CNAS-side run to an upstream log entry. Null when the connector did not
    /// produce one.
    /// </summary>
    public string? UpstreamPullId { get; set; }
}
