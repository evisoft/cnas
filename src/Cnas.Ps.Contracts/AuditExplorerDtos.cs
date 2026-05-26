using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0193 / TOR SEC 052 — request body for the audit-log explorer search endpoint
/// (<c>POST /api/admin/audit/search</c>). Carries the optional QBE envelope, the
/// optional UTC date-range bound, and the paging window the caller wants to
/// materialise.
/// </summary>
/// <remarks>
/// <para>
/// <b>Paging cap.</b> The server clamps <see cref="Take"/> to a hard ceiling of
/// 200 even when the wire envelope carries a larger number — the cap protects
/// the API server from oversized JSON responses and the SIEM forwarder from
/// runaway materialisation. Validators emit a 400 ProblemDetails when the wire
/// value exceeds the cap so the UI sees the rejection before the call hits the
/// service layer.
/// </para>
/// <para>
/// <b>QBE envelope.</b> Reuses the canonical <see cref="QbeFilterDto"/>
/// vocabulary so the explorer UI can compose arbitrary field-level predicates
/// against the <c>AuditLog</c> registry without learning a bespoke query
/// language. <see langword="null"/> or empty means "no QBE narrowing".
/// </para>
/// <para>
/// <b>Sqid invariant.</b> The DTO carries no raw database identifiers — the
/// <c>AuditLog</c> registry exposes no Sqid-bearing fields on the inbound side;
/// outbound rows surface Sqid-encoded ids via <see cref="AuditLogRowDto"/>.
/// </para>
/// </remarks>
/// <param name="Filter">Optional QBE envelope; null treated as "no QBE filter".</param>
/// <param name="FromUtc">
/// Inclusive lower bound on <c>AuditLog.EventAtUtc</c>. When both
/// <see cref="FromUtc"/> and <see cref="ToUtc"/> are supplied, the validator
/// enforces <see cref="FromUtc"/> ≤ <see cref="ToUtc"/>.
/// </param>
/// <param name="ToUtc">
/// Inclusive upper bound on <c>AuditLog.EventAtUtc</c>.
/// </param>
/// <param name="Skip">
/// Zero-based row offset to skip before returning. Must be ≥ 0; the validator
/// rejects negative values.
/// </param>
/// <param name="Take">
/// Maximum number of rows to return. Server-side cap is 200; the validator
/// rejects values above the cap.
/// </param>
public sealed record AuditLogSearchInput(
    QbeFilterDto? Filter = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Skip = 0,
    int Take = 50);

/// <summary>
/// R0193 — Sensitivity-floor declaration for the audit explorer page envelope.
/// Audit details may legitimately surface redacted PII fragments (the redactor
/// substitutes <c>"[redacted]"</c> but operators routinely query rows that
/// originated from sensitive flows), so the page response is uniformly
/// classified as <see cref="SensitivityLabel.Confidential"/> at the type
/// level — the middleware stamps the response with the appropriate header so
/// downstream observability tooling treats the payload accordingly.
/// </summary>
/// <param name="Items">Materialised rows for the requested window.</param>
/// <param name="TotalCount">
/// Total number of rows that matched the filter — distinct from
/// <c>Items.Count</c> so the UI can render an accurate "showing X of Y" hint.
/// </param>
/// <param name="AppliedSuggestions">
/// Free-form advisory messages the service may emit (e.g. "result truncated
/// at 200 rows; narrow your filter"). Always shape-stable (never null).
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record AuditLogPageDto(
    IReadOnlyList<AuditLogRowDto> Items,
    long TotalCount,
    IReadOnlyList<string> AppliedSuggestions);

/// <summary>
/// R0193 — single audit-log row projected for the explorer. All ids are
/// Sqid-encoded per CLAUDE.md RULE 3; the SHA-256 hash chain fields surface as
/// 8-character lowercase hex prefixes so the explorer UI can detect tamper
/// without leaking the full digest (which is operationally interesting to an
/// attacker mining for hash collisions).
/// </summary>
/// <remarks>
/// <para>
/// <b>Sensitivity floor.</b> The type carries
/// <see cref="SensitivityLabel.Confidential"/> because <see cref="DetailsJson"/>
/// MAY carry PII fragments even after the producer-side
/// <c>PiiRedactor</c> pass (the redactor is a substring allow-list; an
/// unfamiliar PII field name slips through until added). The middleware
/// stamps the response header accordingly.
/// </para>
/// <para>
/// <b>Hash prefixes.</b> <see cref="PrevHashHex"/> and <see cref="RowHashHex"/>
/// are the first 8 lowercase hex chars of the SHA-256 digest. The full digest
/// stays server-side; the explorer UI uses the prefix only to highlight
/// chain-break breadcrumbs in the row tooltip.
/// </para>
/// </remarks>
/// <param name="Id">Sqid-encoded primary key.</param>
/// <param name="CreatedAtUtc">UTC instant of the business event (alias for <c>EventAtUtc</c>).</param>
/// <param name="EventCode">Stable event code (e.g. <c>USER.LOGIN.SUCCESS</c>).</param>
/// <param name="Severity">Severity classification — string form of <c>AuditSeverity</c>.</param>
/// <param name="ActorUserSqid">
/// Sqid-encoded actor identifier when the actor id is a numeric user primary
/// key, otherwise the raw string actor id (system / job / anonymous shows
/// verbatim). The audit pipeline accepts either shape.
/// </param>
/// <param name="ResourceType">Affected entity kind (nullable).</param>
/// <param name="ResourceSqid">
/// Sqid-encoded affected-entity primary key (nullable). Null when the original
/// row carried no <c>TargetEntityId</c>.
/// </param>
/// <param name="DetailsJson">Structured details — already PII-redacted by the producer.</param>
/// <param name="PrevHashHex">First 8 lowercase hex chars of the previous-row SHA-256 digest.</param>
/// <param name="RowHashHex">First 8 lowercase hex chars of this row's SHA-256 digest.</param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record AuditLogRowDto(
    string Id,
    DateTime CreatedAtUtc,
    string EventCode,
    string Severity,
    string? ActorUserSqid,
    string? ResourceType,
    string? ResourceSqid,
    string DetailsJson,
    string PrevHashHex,
    string RowHashHex);

/// <summary>
/// R0193 — summary report returned by
/// <c>POST /api/admin/audit/archives/{archiveKey}/import</c>. The route
/// re-attaches an archived audit batch (R0188) onto the live AuditLog table
/// by replaying its records through the standard projector + chain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency.</b> The importer skips rows that already exist in the
/// AuditLog table — equality is computed on the natural composite key
/// <c>(EventAtUtc, EventCode, ActorId, TargetEntityId)</c>. A re-import of the
/// same archive file produces <c>RowsImported=0</c> and
/// <c>RowsSkipped={archive size}</c>.
/// </para>
/// <para>
/// <b>Hash chain.</b> Imported rows are chained from the current tail of the
/// AuditLog table (the same path used by <c>AuditDrainer</c> and
/// <c>AuditArchiveReplayJob</c>) so the on-disk chain remains continuous.
/// </para>
/// </remarks>
/// <param name="RowsImported">Number of rows actually inserted.</param>
/// <param name="RowsSkipped">Number of rows skipped as duplicates.</param>
/// <param name="FirstUtc">UTC instant of the earliest row in the archive; null for empty archives.</param>
/// <param name="LastUtc">UTC instant of the latest row in the archive; null for empty archives.</param>
/// <param name="ArchiveKey">Opaque archive identifier echoed back to the caller.</param>
public sealed record AuditArchiveImportSummaryDto(
    int RowsImported,
    int RowsSkipped,
    DateTime? FirstUtc,
    DateTime? LastUtc,
    string ArchiveKey);
