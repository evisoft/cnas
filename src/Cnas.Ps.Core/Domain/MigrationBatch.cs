namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2430 / R2431 / TOR M4 — one row per processed batch within a
/// <see cref="MigrationRun"/>. The importer flushes counters per batch so
/// operators can drill into the per-batch throughput when investigating a
/// slow or partially-failed run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural-key uniqueness.</b> The EF configuration enforces a unique
/// constraint on <c>(RunId, BatchOrdinal)</c> — each batch is recorded once
/// regardless of retries.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because operators
/// reference an individual batch by Sqid through the admin surface.
/// </para>
/// </remarks>
public sealed class MigrationBatch : AuditableEntity, IExternalId
{
    /// <summary>Foreign key to the parent <see cref="MigrationRun"/>.</summary>
    public long RunId { get; set; }

    /// <summary>1-based ordinal of this batch within the parent run.</summary>
    public int BatchOrdinal { get; set; }

    /// <summary>Count of source rows the batch contained.</summary>
    public int RowsInBatch { get; set; }

    /// <summary>Count of staging rows persisted as Imported within this batch.</summary>
    public int RowsImported { get; set; }

    /// <summary>Count of staging rows persisted as Updated within this batch.</summary>
    public int RowsUpdated { get; set; }

    /// <summary>Count of rows the mapper marked as Skipped within this batch.</summary>
    public int RowsSkipped { get; set; }

    /// <summary>Count of source rows that produced Critical findings within this batch.</summary>
    public int RowsFailed { get; set; }

    /// <summary>Wall-clock duration of the batch in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>UTC instant the batch was finalised by the importer.</summary>
    public DateTime ProcessedAt { get; set; }
}
