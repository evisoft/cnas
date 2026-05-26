using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R1710 / TOR INT 002 / Annex 4 — DTOs for the offline-batch (file-based)
// equivalents of the synchronous Annex-4 endpoints. A B2B consumer uploads a
// request CSV, the system queues a job, processes each row against the same
// underlying query as the synchronous endpoint, signs the response CSV, and
// makes it available for download.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R1710 / TOR INT 002 — submission input envelope. The consumer subject is
/// filled by the controller from the auth context — any value supplied by
/// the client is overwritten. Treated as a required field in the application
/// layer so internal callers (tests, replay tooling) carry an explicit
/// subject.
/// </summary>
/// <param name="ConsumerSubject">
/// Opaque identifier of the B2B consumer (OAuth client id / MConnect subject).
/// Server-filled — clients cannot influence this value.
/// </param>
/// <param name="OpCode">
/// Stable enum-name of the targeted Annex-4 op (e.g. <c>GetInsuredPersonStatus</c>).
/// </param>
/// <param name="RequestFileName">
/// Original CSV filename the consumer uploaded — used only on the
/// <c>Content-Disposition</c> of the response download.
/// </param>
/// <param name="RequestFileBytes">
/// Raw bytes of the uploaded request CSV. 1 byte ≤ size ≤ 10 MB.
/// </param>
/// <param name="RequestFileHashSha256">
/// Hex-encoded SHA-256 hash of <see cref="RequestFileBytes"/>. Validated
/// against the actual bytes to detect transport corruption.
/// </param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record OfflineBatchSubmissionInputDto(
    string ConsumerSubject,
    string OpCode,
    string RequestFileName,
    byte[] RequestFileBytes,
    string RequestFileHashSha256);

/// <summary>
/// R1710 / TOR INT 002 — outbound projection of an
/// <c>OfflineBatchSubmission</c>. The <see cref="Id"/> field is a Sqid; raw
/// long ids never leave the system.
/// </summary>
/// <param name="Id">Sqid-encoded submission id.</param>
/// <param name="BatchNumber">Stable human-readable batch number (<c>OBS-{year}-{seq:000000}</c>).</param>
/// <param name="ConsumerSubject">Opaque consumer subject.</param>
/// <param name="OpCode">Stable enum-name of the targeted Annex-4 op.</param>
/// <param name="Status">Stable enum-name of the current lifecycle status.</param>
/// <param name="RequestFileName">Original uploaded filename.</param>
/// <param name="RequestFileSizeBytes">Size of the uploaded request CSV.</param>
/// <param name="RequestFileHashSha256">SHA-256 hash of the uploaded request CSV.</param>
/// <param name="RequestRowCount">Count of data rows parsed from the request CSV.</param>
/// <param name="ResponseFileHashSha256">SHA-256 hash of the generated response CSV (null until <c>Completed</c>).</param>
/// <param name="ResponseFileSignatureBase64">Base64 HMAC-SHA256 signature of the response CSV (null until <c>Completed</c>).</param>
/// <param name="SubmittedAt">UTC submission timestamp.</param>
/// <param name="StartedAt">UTC timestamp the processor began running this batch.</param>
/// <param name="CompletedAt">UTC timestamp the processor finalised this batch.</param>
/// <param name="FailureReason">Sanitised processor failure reason — populated only when <c>Status</c> is <c>Failed</c>.</param>
/// <param name="TotalRowsProcessed">Count of rows finalised (succeeded + failed).</param>
/// <param name="TotalRowsFailed">Count of rows that ended in <c>Failed</c>.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record OfflineBatchSubmissionDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string BatchNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ConsumerSubject,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string OpCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RequestFileName,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long RequestFileSizeBytes,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RequestFileHashSha256,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RequestRowCount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ResponseFileHashSha256,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ResponseFileSignatureBase64,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime SubmittedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? StartedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? CompletedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? FailureReason,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int TotalRowsProcessed,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int TotalRowsFailed);

/// <summary>
/// R1710 / TOR INT 002 — filter envelope for submissions list lookups.
/// </summary>
/// <param name="ConsumerSubject">Optional consumer subject filter (admin surface only — the consumer-facing endpoint pins the caller's subject).</param>
/// <param name="OpCode">Optional op-code filter (stable enum-name string).</param>
/// <param name="Status">Optional status filter (stable enum-name string).</param>
/// <param name="SubmittedAfter">Optional inclusive lower bound on <c>SubmittedAt</c>.</param>
/// <param name="SubmittedBefore">Optional inclusive upper bound on <c>SubmittedAt</c>.</param>
/// <param name="Skip">Page offset (default 0; must be ≥ 0).</param>
/// <param name="Take">Page size (default 50; max 100).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record OfflineBatchSubmissionFilterDto(
    string? ConsumerSubject = null,
    string? OpCode = null,
    string? Status = null,
    DateTime? SubmittedAfter = null,
    DateTime? SubmittedBefore = null,
    int Skip = 0,
    int Take = 50);

/// <summary>
/// R1710 / TOR INT 002 — paged envelope returned by the list endpoint.
/// </summary>
/// <param name="Items">Submissions on the requested page.</param>
/// <param name="Total">Total matching submissions across all pages.</param>
/// <param name="Skip">Page offset that was applied.</param>
/// <param name="Take">Page size that was applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record OfflineBatchSubmissionPageDto(
    IReadOnlyList<OfflineBatchSubmissionDto> Items,
    int Total,
    int Skip,
    int Take);

/// <summary>
/// R1710 / TOR INT 002 — outbound projection of one
/// <c>OfflineBatchRow</c>. Row payloads are not echoed via this DTO so the
/// admin list endpoint cannot become an exfiltration channel for the per-
/// row JSON snapshots — operators fetch the full response CSV through the
/// download endpoint instead.
/// </summary>
/// <param name="RowOrdinal">1-based position inside the request CSV.</param>
/// <param name="Status">Stable enum-name of the row status.</param>
/// <param name="ErrorCode">Stable error code from the underlying interop call (null on success).</param>
/// <param name="ErrorDescription">Short, PII-free description of the failure (null on success).</param>
/// <param name="ProcessedAt">UTC timestamp the processor finalised this row.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record OfflineBatchRowDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowOrdinal,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? ErrorCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ErrorDescription,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? ProcessedAt);

/// <summary>
/// R1710 / TOR INT 002 — filter envelope for row-list lookups inside one
/// submission.
/// </summary>
/// <param name="Status">Optional row-status filter (stable enum-name string).</param>
/// <param name="Skip">Page offset (default 0; must be ≥ 0).</param>
/// <param name="Take">Page size (default 100; max 200).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record OfflineBatchRowFilterDto(
    string? Status = null,
    int Skip = 0,
    int Take = 100);

/// <summary>
/// R1710 / TOR INT 002 — paged envelope returned by the rows-list endpoint.
/// </summary>
/// <param name="Items">Rows on the requested page.</param>
/// <param name="Total">Total matching rows across all pages.</param>
/// <param name="Skip">Page offset that was applied.</param>
/// <param name="Take">Page size that was applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record OfflineBatchRowPageDto(
    IReadOnlyList<OfflineBatchRowDto> Items,
    int Total,
    int Skip,
    int Take);

/// <summary>
/// R1710 / TOR INT 002 — descriptor returned by the
/// <c>/download</c> endpoint preview. The caller invokes the
/// <see cref="DownloadUrl"/> with appropriate credentials to stream the
/// response CSV.
/// </summary>
/// <param name="DownloadUrl">Relative API URL of the download endpoint.</param>
/// <param name="FileName">Suggested attachment filename (<c>{batchNumber}-response.csv</c>).</param>
/// <param name="ContentType">Always <c>text/csv</c>.</param>
/// <param name="SizeBytes">Size of the response CSV in bytes.</param>
/// <param name="HashSha256">Hex-encoded SHA-256 hash of the response CSV.</param>
/// <param name="SignatureBase64">Base64 HMAC-SHA256 signature of the response CSV.</param>
/// <param name="SignedAt">UTC timestamp at which the signature was computed (equals <c>CompletedAt</c>).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record OfflineBatchDownloadInfoDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DownloadUrl,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string FileName,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ContentType,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long SizeBytes,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string HashSha256,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string SignatureBase64,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime SignedAt);

/// <summary>
/// R1710 / TOR INT 002 — reason envelope for cancellation requests.
/// </summary>
/// <param name="Reason">Free-form rationale (3..500 characters).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record OfflineBatchReasonInputDto(string Reason);
