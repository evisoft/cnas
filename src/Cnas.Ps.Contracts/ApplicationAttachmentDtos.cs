namespace Cnas.Ps.Contracts;

/// <summary>
/// R0322 / TOR UI 014 — outbound projection of one <c>ApplicationAttachment</c>
/// row. All ids are Sqid-encoded per CLAUDE.md RULE 3; both enum fields are
/// rendered as their stable string names so the client UI can switch on a
/// self-describing label without owning the numeric mapping.
/// </summary>
/// <param name="Id">Sqid-encoded id of the attachment-link row.</param>
/// <param name="ApplicationSqid">Sqid-encoded id of the parent application.</param>
/// <param name="DocumentSqid">Sqid-encoded id of the linked document.</param>
/// <param name="Category">
/// Stable category enum name (<c>Identity</c>, <c>Income</c>, <c>MedicalReport</c>,
/// <c>Birth</c>, <c>Death</c>, <c>Marriage</c>, <c>Custody</c>, <c>Other</c>).
/// </param>
/// <param name="IsMandatorySnapshot">
/// Snapshot of "was this document type mandatory at attach time?" — captured from
/// the service-passport intake checklist at attach time.
/// </param>
/// <param name="AttachedByUserSqid">Sqid-encoded id of the user who attached this document.</param>
/// <param name="AttachedAtUtc">UTC instant the attachment link was created.</param>
/// <param name="VirusScanStatus">
/// Stable virus-scan status enum name (<c>Pending</c>, <c>Clean</c>, <c>Infected</c>,
/// <c>ScanFailed</c>, <c>Skipped</c>).
/// </param>
/// <param name="VirusScannedAtUtc">UTC instant the scan completed; null while Pending.</param>
/// <param name="VirusScannerName">Name / version tag of the scanner that produced the result.</param>
/// <param name="Notes">Optional free-form annotation (≤ 500 chars).</param>
/// <param name="RemovedAtUtc">UTC instant the attachment was logically removed; null while active.</param>
/// <param name="RemovedByUserSqid">Sqid-encoded id of the operator who removed the link.</param>
/// <param name="RemovalReason">Operator-supplied removal justification.</param>
public sealed record ApplicationAttachmentDto(
    string Id,
    string ApplicationSqid,
    string DocumentSqid,
    string Category,
    bool IsMandatorySnapshot,
    string AttachedByUserSqid,
    DateTime AttachedAtUtc,
    string VirusScanStatus,
    DateTime? VirusScannedAtUtc,
    string? VirusScannerName,
    string? Notes,
    DateTime? RemovedAtUtc,
    string? RemovedByUserSqid,
    string? RemovalReason);

/// <summary>
/// R0322 — input shape accepted by <c>POST /api/applications/{sqid}/attachments</c>
/// to attach an existing <c>Document</c> to an application with rich metadata.
/// The uploader identity is resolved server-side from the authenticated principal
/// (CLAUDE.md §2.4 — mass-assignment prevention).
/// </summary>
/// <param name="DocumentSqid">Sqid-encoded id of the existing document to attach.</param>
/// <param name="Category">
/// Stable category enum name. Must be one of the values on
/// <c>ApplicationAttachmentCategory</c>; validator rejects unknown values.
/// </param>
/// <param name="IsMandatorySnapshot">
/// Snapshot of "is this document type mandatory" captured from the service-passport
/// intake checklist at attach time.
/// </param>
/// <param name="Notes">Optional free-form annotation (≤ 500 chars).</param>
public sealed record ApplicationAttachInputDto(
    string DocumentSqid,
    string Category,
    bool IsMandatorySnapshot,
    string? Notes);

/// <summary>
/// R0322 — input shape accepted by <c>POST /api/attachments/{sqid}/remove</c>
/// to soft-remove an attachment-link row. The underlying document is untouched.
/// </summary>
/// <param name="Reason">Operator-supplied removal justification (3..500 chars).</param>
public sealed record ApplicationAttachmentReasonInputDto(string Reason);

/// <summary>
/// R0322 — input shape accepted by <c>POST /api/attachments/{sqid}/scan-result</c>.
/// Posted by the virus-scan worker after evaluating the attached document. The
/// endpoint is gated to <c>cnas-tech-admin</c> per the route registration.
/// </summary>
/// <param name="Status">
/// Stable virus-scan status enum name. Must be one of <c>Clean</c>, <c>Infected</c>,
/// <c>ScanFailed</c>, <c>Skipped</c> (<c>Pending</c> is rejected — that's the row's
/// birth state, not a terminal result).
/// </param>
/// <param name="ScannerName">Name / version tag of the scanner (≤ 64 chars).</param>
/// <param name="Notes">Optional scanner-supplied annotation (≤ 500 chars).</param>
public sealed record ApplicationAttachmentScanResultInputDto(
    string Status,
    string ScannerName,
    string? Notes);

/// <summary>
/// R0322 — filter shape consumed by
/// <c>GET /api/applications/{sqid}/attachments</c>. All fields are optional.
/// </summary>
/// <param name="Category">
/// Optional category-enum-name filter (e.g. <c>"Identity"</c>); null matches all
/// categories.
/// </param>
/// <param name="VirusScanStatus">
/// Optional virus-scan-status filter (e.g. <c>"Clean"</c>); null matches all statuses.
/// </param>
/// <param name="IncludeRemoved">
/// When true, includes rows with <c>RemovedAtUtc != null</c>. Defaults to false.
/// </param>
/// <param name="Skip">0-based offset into the result set (≥ 0).</param>
/// <param name="Take">Page size, clamped to <c>1..200</c> in the service layer.</param>
public sealed record ApplicationAttachmentFilterDto(
    string? Category,
    string? VirusScanStatus,
    bool IncludeRemoved,
    int Skip,
    int Take);

/// <summary>
/// R0322 — paged result wrapper for the per-application attachment listing.
/// </summary>
/// <param name="Items">Page of attachment rows ordered <c>AttachedAtUtc DESC</c>.</param>
/// <param name="TotalCount">Total number of rows matching the filter.</param>
/// <param name="Skip">Echoed back the request's <c>skip</c> offset.</param>
/// <param name="Take">Echoed back the request's <c>take</c> page size.</param>
public sealed record ApplicationAttachmentPageDto(
    IReadOnlyList<ApplicationAttachmentDto> Items,
    long TotalCount,
    int Skip,
    int Take);
