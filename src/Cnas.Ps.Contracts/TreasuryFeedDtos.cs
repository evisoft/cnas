using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R1810 / TOR BP 1.2-I — DTOs for the daily Treasury feed import registry.
// Operators surface these via the admin REST surface to audit a day's BASS
// receipts ingestion. Every <c>Id</c> field is Sqid-encoded per CLAUDE.md
// RULE 3; the one documented exception is <c>MappedReceiptId</c> on the row
// DTO which keeps a raw long for the internal-ops correlation use case.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R1810 / TOR BP 1.2-I — outbound projection of a
/// <c>TreasuryFeedImport</c>. Carries the lifecycle status, the source
/// descriptor, file metadata, and the per-row counters.
/// </summary>
/// <param name="Id">Sqid-encoded import id.</param>
/// <param name="FeedDate">Calendar date the feed covers.</param>
/// <param name="Status">Stable enum-name of the current lifecycle status.</param>
/// <param name="SourceKind">Stable enum-name of the feed source.</param>
/// <param name="SourceReference">Sanitised source descriptor (URL / path / filename); never carries credentials.</param>
/// <param name="FileSizeBytes">Size of the downloaded file in bytes. Null while in flight.</param>
/// <param name="FileHashSha256">Hex-encoded SHA-256 hash of the downloaded file. Null while in flight.</param>
/// <param name="RowsTotal">Count of data rows parsed (excludes header).</param>
/// <param name="RowsImported">Count of rows that produced a new TreasuryPaymentReceipt insert.</param>
/// <param name="RowsUpdated">Count of rows that updated an existing receipt.</param>
/// <param name="RowsSkipped">Count of rows whose content already matched an existing receipt (no-op).</param>
/// <param name="RowsFailed">Count of rows that failed validation or parsing.</param>
/// <param name="StartedAt">UTC instant the importer began this run.</param>
/// <param name="CompletedAt">UTC instant the importer finalised this run. Null while in flight.</param>
/// <param name="FailureReason">Sanitised PII-free failure reason. Null on success.</param>
/// <param name="TriggerKind">Stable enum-name of the run's trigger origin.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record TreasuryFeedImportDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateOnly FeedDate,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string SourceKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? SourceReference,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long? FileSizeBytes,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? FileHashSha256,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowsTotal,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowsImported,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowsUpdated,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowsSkipped,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowsFailed,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime StartedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? CompletedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? FailureReason,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string TriggerKind);

/// <summary>
/// R1810 / TOR BP 1.2-I — compact summary returned by the importer right
/// after finalising a run. Carries only the public-counter facets needed by
/// callers (the Quartz job logs them; the manual admin surface displays them).
/// </summary>
/// <param name="Id">Sqid-encoded import id.</param>
/// <param name="FeedDate">Calendar date the feed covers.</param>
/// <param name="Status">Stable enum-name of the terminal status.</param>
/// <param name="RowsTotal">Count of data rows parsed.</param>
/// <param name="RowsImported">Count of rows that produced inserts.</param>
/// <param name="RowsUpdated">Count of rows that updated existing receipts.</param>
/// <param name="RowsSkipped">Count of idempotent no-op rows.</param>
/// <param name="RowsFailed">Count of failed rows.</param>
/// <param name="TriggerKind">Stable enum-name of the run's trigger origin.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record TreasuryFeedImportSummaryDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateOnly FeedDate,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowsTotal,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowsImported,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowsUpdated,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowsSkipped,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowsFailed,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string TriggerKind);

/// <summary>
/// R1810 / TOR BP 1.2-I — outbound projection of one
/// <c>TreasuryFeedImportRow</c>. Row payloads are NOT echoed via this DTO so
/// the admin list endpoint cannot become an exfiltration channel for raw
/// payer data — operators correlate via <c>MappedReceiptId</c> instead.
/// </summary>
/// <param name="Id">Sqid-encoded row id.</param>
/// <param name="RowOrdinal">1-based position inside the feed file.</param>
/// <param name="Status">Stable enum-name of the row status.</param>
/// <param name="MappedReceiptId">
/// Raw internal id of the resulting TreasuryPaymentReceipt — documented
/// internal-ops exception to CLAUDE.md RULE 3 (see entity remarks).
/// </param>
/// <param name="ErrorCode">Stable code categorising a row-level failure. Null on success.</param>
/// <param name="ErrorDescription">Short, PII-free description of the failure. Null on success.</param>
/// <param name="ProcessedAt">UTC instant the importer finalised this row.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record TreasuryFeedImportRowDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowOrdinal,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    long? MappedReceiptId,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? ErrorCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ErrorDescription,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? ProcessedAt);

/// <summary>
/// R1810 / TOR BP 1.2-I — filter envelope used by the rows-page lookup.
/// </summary>
/// <param name="Status">Optional row-status filter (stable enum-name string).</param>
/// <param name="Skip">Page offset (default 0; must be ≥ 0).</param>
/// <param name="Take">Page size (default 100; max 200).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record TreasuryFeedImportRowFilterDto(
    string? Status = null,
    int Skip = 0,
    int Take = 100);

/// <summary>
/// R1810 / TOR BP 1.2-I — paged rows envelope used by the import-details
/// endpoint.
/// </summary>
/// <param name="Items">Rows on the requested page.</param>
/// <param name="Total">Total matching rows across all pages.</param>
/// <param name="Skip">Page offset that was applied.</param>
/// <param name="Take">Page size that was applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record TreasuryFeedImportRowPageDto(
    IReadOnlyList<TreasuryFeedImportRowDto> Items,
    int Total,
    int Skip,
    int Take);

/// <summary>
/// R1810 / TOR BP 1.2-I — details envelope (import + paged rows) returned
/// by the import-details endpoint.
/// </summary>
/// <param name="Import">The import summary.</param>
/// <param name="Rows">Paged subset of the import's parsed rows.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record TreasuryFeedImportDetailsDto(
    TreasuryFeedImportDto Import,
    TreasuryFeedImportRowPageDto Rows);

/// <summary>
/// R1810 / TOR BP 1.2-I — filter envelope for the imports list endpoint.
/// </summary>
/// <param name="Status">Optional status filter (stable enum-name string).</param>
/// <param name="FeedDateFrom">Optional inclusive lower bound on <c>FeedDate</c>.</param>
/// <param name="FeedDateTo">Optional inclusive upper bound on <c>FeedDate</c>.</param>
/// <param name="TriggerKind">Optional trigger-kind filter (stable enum-name string).</param>
/// <param name="Skip">Page offset (default 0; must be ≥ 0).</param>
/// <param name="Take">Page size (default 50; max 100).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record TreasuryFeedImportFilterDto(
    string? Status = null,
    DateOnly? FeedDateFrom = null,
    DateOnly? FeedDateTo = null,
    string? TriggerKind = null,
    int Skip = 0,
    int Take = 50);

/// <summary>
/// R1810 / TOR BP 1.2-I — paged envelope returned by the imports list
/// endpoint.
/// </summary>
/// <param name="Items">Imports on the requested page.</param>
/// <param name="Total">Total matching imports across all pages.</param>
/// <param name="Skip">Page offset that was applied.</param>
/// <param name="Take">Page size that was applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record TreasuryFeedImportPageDto(
    IReadOnlyList<TreasuryFeedImportDto> Items,
    int Total,
    int Skip,
    int Take);

/// <summary>
/// R1810 / TOR BP 1.2-I — input envelope for the manual-import admin
/// endpoint. The feed date is supplied by the operator and must satisfy the
/// validator's not-in-future + not-older-than-365-days bounds.
/// </summary>
/// <param name="FeedDate">Calendar date the manual import should target.</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record TreasuryFeedManualImportInputDto(DateOnly FeedDate);
