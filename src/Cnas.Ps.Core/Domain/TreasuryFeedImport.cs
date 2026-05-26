namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R1810 / TOR BP 1.2-I — one row per Treasury feed import attempt. The
/// nightly <c>TreasuryFeedImportJob</c> inserts a row, advances its
/// <see cref="Status"/> through the lifecycle, and finalises it with the
/// per-row counters. Operators surface this row via the admin REST surface
/// to audit a day's ingestion.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b>
/// <see cref="TreasuryFeedImportStatus.Pending"/> →
/// <see cref="TreasuryFeedImportStatus.Downloading"/> →
/// <see cref="TreasuryFeedImportStatus.Parsing"/> →
/// <see cref="TreasuryFeedImportStatus.Importing"/> →
/// <see cref="TreasuryFeedImportStatus.Completed"/>. A failure at any stage
/// flips the row to <see cref="TreasuryFeedImportStatus.Failed"/> with a
/// sanitised <see cref="FailureReason"/>; the scheduler also has the option
/// to record a <see cref="TreasuryFeedImportStatus.Skipped"/> row when a
/// completed import already exists for the date.
/// </para>
/// <para>
/// <b>Natural-key uniqueness.</b> A filtered unique index on
/// <c>(FeedDate, SourceKind)</c> WHERE <c>Status = 'Completed'</c> prevents
/// double-ingest of the same day's feed while still allowing retries of a
/// previously failed import.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because operators
/// reference an individual import by Sqid through the admin surface.
/// </para>
/// <para>
/// <b>No PII.</b> The row carries only counters, hashes, and sanitised
/// references — no payer IDNOs / names / amounts leak into this aggregate.
/// </para>
/// </remarks>
public sealed class TreasuryFeedImport : AuditableEntity, IExternalId
{
    /// <summary>The calendar date the feed covers (typically yesterday at job runtime).</summary>
    public DateOnly FeedDate { get; set; }

    /// <summary>Current lifecycle status; defaults to <see cref="TreasuryFeedImportStatus.Pending"/>.</summary>
    public TreasuryFeedImportStatus Status { get; set; } = TreasuryFeedImportStatus.Pending;

    /// <summary>Origin of the feed bytes consumed by this run.</summary>
    public TreasuryFeedSourceKind SourceKind { get; set; }

    /// <summary>
    /// Sanitised source descriptor — URL, SFTP path, or upload filename. No
    /// credentials, query strings, or tokens. Bounded by validators to ≤ 512
    /// characters.
    /// </summary>
    public string? SourceReference { get; set; }

    /// <summary>Size of the downloaded file in bytes. Populated once the source completes the fetch.</summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>Hex-encoded SHA-256 hash of the downloaded file (64 lower-case hex chars).</summary>
    public string? FileHashSha256 { get; set; }

    /// <summary>Count of data rows parsed from the file (excludes the header row).</summary>
    public int RowsTotal { get; set; }

    /// <summary>Count of rows that resulted in a new <c>TreasuryPaymentReceipt</c> insert.</summary>
    public int RowsImported { get; set; }

    /// <summary>Count of rows that updated an existing receipt (idempotent path).</summary>
    public int RowsUpdated { get; set; }

    /// <summary>Count of rows whose content already matched the existing receipt — no write occurred.</summary>
    public int RowsSkipped { get; set; }

    /// <summary>Count of rows that failed validation or parsing.</summary>
    public int RowsFailed { get; set; }

    /// <summary>UTC instant the importer began this run.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC instant the importer transitioned the row to a terminal state. Null while in flight.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Sanitised, PII-free failure reason. Bounded by validators to ≤ 1000 characters. Null on success.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Origin of the run that produced this row.</summary>
    public TreasuryFeedTriggerKind TriggerKind { get; set; }
}
