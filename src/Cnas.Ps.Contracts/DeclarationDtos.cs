using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0810 / R0811 / R0812 / R0813 — Declarații (contribution declarations)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0810 / R0811 / R0812 — one declaration row as it leaves the system. The
/// shape is identical regardless of the originating registration path; the
/// <see cref="Kind"/> field communicates the upstream document family.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying declaration row.</param>
/// <param name="ContributorSqid">Sqid-encoded id of the owning payer (Plătitor).</param>
/// <param name="Kind">
/// Stable enum-name representation of the
/// <c>Cnas.Ps.Core.Domain.DeclarationKind</c> value (<c>Sfs</c>,
/// <c>BassFour</c>, ..., <c>Other</c>). The wire shape is the enum name (not
/// the numeric value) so clients can switch on a self-describing label.
/// </param>
/// <param name="ReportingMonth">Calendar month the row covers (day = 1).</param>
/// <param name="FiledAtUtc">UTC instant the declaration was filed.</param>
/// <param name="ReferenceNumber">External reference assigned upstream, when present.</param>
/// <param name="DeclaredContributionAmount">Gross amount declared (MDL).</param>
/// <param name="AdjustedContributionAmount">
/// Supersession amount populated after an
/// <c>IDeclarationService.AdjustAsync</c> correction; <c>null</c> while the
/// row carries the original declaration unchanged.
/// </param>
/// <param name="Status">
/// Stable enum-name representation of the
/// <c>Cnas.Ps.Core.Domain.DeclarationStatus</c> value (<c>Received</c>,
/// <c>Validated</c>, <c>Adjusted</c>, <c>Cancelled</c>).
/// </param>
/// <param name="Notes">Operator note attached to the row (3..500 chars when set).</param>
/// <param name="IsArchived">True when the row has been soft-archived after a reporting period closes.</param>
/// <param name="HasScannedCopy">
/// R0821 / R0823 / Annex 1 §8.1.3 — true when the row has at least one scanned
/// copy attached via the R0227 attachment pipeline. The explorer endpoint
/// (R0822) filters on this flag to surface paper-trail-backed rows.
/// </param>
/// <param name="OcrConfidenceLevel">
/// R0821 / R0823 / Annex 1 §8.1.3 — categorical OCR-confidence band, one of
/// <c>"High"</c>, <c>"Medium"</c>, <c>"Low"</c>, or <see langword="null"/> when
/// no OCR took place. Drives the badge rendering in the future explorer UI.
/// </param>
/// <param name="RegisteredByOffice">
/// R0823 / Annex 1 §8.1.3 — code of the CNAS office that registered the row
/// (mirrors <c>CnasBranch.Code</c>). Null on SFS-feed rows and historic
/// imports.
/// </param>
/// <param name="FormVersion">
/// R0823 / Annex 1 §8.1.3 — declaration-form version identifier (e.g.
/// <c>"4-BASS-v2018"</c>). Null on historic rows ingested before the catalogue
/// landed.
/// </param>
public sealed record DeclarationDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ContributorSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Kind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly ReportingMonth,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime FiledAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ReferenceNumber,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal DeclaredContributionAmount,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? AdjustedContributionAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool IsArchived,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool HasScannedCopy = false,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? OcrConfidenceLevel = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? RegisteredByOffice = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? FormVersion = null);

/// <summary>
/// R0810 / BP 1.2-A — input DTO for registering a declaration from the
/// automated monthly SI SFS feed. The service implicitly stamps
/// <c>Kind = DeclarationKind.Sfs</c> — callers must not supply it.
/// </summary>
/// <param name="ContributorSqid">Sqid-encoded payer id.</param>
/// <param name="ReportingMonth">Calendar month the row covers (day = 1).</param>
/// <param name="ReferenceNumber">External SFS document id; required by validator.</param>
/// <param name="DeclaredContributionAmount">Gross amount declared (MDL).</param>
/// <param name="Notes">Optional operator note (3..500 chars when supplied).</param>
/// <param name="FiledAtUtc">
/// Optional override of the filing instant. When <c>null</c> the service uses
/// the current UTC clock.
/// </param>
public sealed record DeclarationFromSfsInputDto(
    string ContributorSqid,
    DateOnly ReportingMonth,
    string ReferenceNumber,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal DeclaredContributionAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes = null,
    DateTime? FiledAtUtc = null);

/// <summary>
/// R0811 / BP 1.2-B — input DTO for registering a paper declaration submitted
/// at a CNAS desk. The validator restricts <see cref="Kind"/> to
/// <c>{BassFour, Bass, BassAn, Pre2018}</c>.
/// </summary>
/// <param name="ContributorSqid">Sqid-encoded payer id.</param>
/// <param name="Kind">
/// Stable <c>DeclarationKind</c> enum name (case-sensitive). Must be one of
/// <c>BassFour</c>, <c>Bass</c>, <c>BassAn</c>, <c>Pre2018</c>.
/// </param>
/// <param name="ReportingMonth">Calendar month the row covers (day = 1).</param>
/// <param name="ReferenceNumber">CNAS form serial; optional for legacy rows.</param>
/// <param name="DeclaredContributionAmount">Gross amount declared (MDL).</param>
/// <param name="Notes">Optional operator note (3..500 chars when supplied).</param>
/// <param name="FiledAtUtc">Optional override of the filing instant.</param>
public sealed record DeclarationAtCnasInputDto(
    string ContributorSqid,
    string Kind,
    DateOnly ReportingMonth,
    string? ReferenceNumber,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal DeclaredContributionAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes = null,
    DateTime? FiledAtUtc = null);

/// <summary>
/// R0812 / BP 1.2-C — input DTO for registering a contribution computed from
/// another supporting document (control / inspection report, court decision,
/// or other). The validator restricts <see cref="Kind"/> to <c>{Control,
/// CourtDecision, Other}</c>.
/// </summary>
/// <param name="ContributorSqid">Sqid-encoded payer id.</param>
/// <param name="Kind">
/// Stable <c>DeclarationKind</c> enum name. Must be one of <c>Control</c>,
/// <c>CourtDecision</c>, <c>Other</c>.
/// </param>
/// <param name="ReportingMonth">Calendar month the row covers (day = 1).</param>
/// <param name="ReferenceNumber">External document reference; optional.</param>
/// <param name="DeclaredContributionAmount">Gross amount declared (MDL).</param>
/// <param name="Notes">Optional operator note (3..500 chars when supplied).</param>
/// <param name="FiledAtUtc">Optional override of the document instant.</param>
public sealed record DeclarationFromOtherDocumentInputDto(
    string ContributorSqid,
    string Kind,
    DateOnly ReportingMonth,
    string? ReferenceNumber,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal DeclaredContributionAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes = null,
    DateTime? FiledAtUtc = null);

/// <summary>
/// R0810-R0812 — input DTO for the <c>POST .../adjust</c> endpoint. The
/// reason is preserved verbatim on the row's <c>Notes</c> column and on the
/// audit trail.
/// </summary>
/// <param name="AdjustedAmount">New amount that supersedes the original declared figure (MDL, ≥ 0).</param>
/// <param name="Reason">Operator-supplied rationale (3..500 chars).</param>
public sealed record DeclarationAdjustInputDto(
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal AdjustedAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);

/// <summary>
/// R0810-R0812 — input DTO for the <c>POST .../cancel</c> endpoint. Cancelled
/// rows are excluded from R0813 monthly totals.
/// </summary>
/// <param name="Reason">Operator-supplied cancellation rationale (3..500 chars).</param>
public sealed record DeclarationCancelInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);

/// <summary>
/// R0813 / BP 1.2-D — per-payer per-month roll-up returned by the monthly
/// contribution calculator.
/// </summary>
/// <param name="Id">Sqid-encoded id of the underlying calculation row.</param>
/// <param name="ContributorSqid">Sqid-encoded payer id.</param>
/// <param name="Month">Calendar month the calculation covers (day = 1).</param>
/// <param name="TotalDeclared">Sum of declared amounts (MDL).</param>
/// <param name="TotalAdjusted">Sum of adjusted-or-declared amounts (MDL).</param>
/// <param name="OverpaymentAmount">Positive when adjusted &lt; declared; <c>null</c> otherwise.</param>
/// <param name="UnderpaymentAmount">Positive when adjusted &gt; declared; <c>null</c> otherwise.</param>
/// <param name="DeclarationCount">Number of non-cancelled declarations rolled into the row.</param>
/// <param name="CalculatedAtUtc">UTC instant the calculation was last produced.</param>
public sealed record MonthlyContributionCalculationDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ContributorSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly Month,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal TotalDeclared,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal TotalAdjusted,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? OverpaymentAmount,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? UnderpaymentAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int DeclarationCount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime CalculatedAtUtc);

// ────────────────────────────────────────────────────────────────────────────
// R0821 / BP 1.2-L — scanned-copy attachment for an existing Declaration row.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0821 / BP 1.2-L / Annex 1 §8.1.3 — input DTO for the
/// <c>POST /api/declarations/{sqid}/scanned-copy</c> endpoint. Carries the
/// caller-supplied bytes (base64-encoded), declared filename, and optional OCR
/// metadata pre-computed by the calling pipeline.
/// </summary>
/// <remarks>
/// The R0821 step is the link between the paper declaration and the row
/// persisted by R0810 / R0811 / R0812. The eventual production OCR pipeline
/// (Tesseract / Azure Document Intelligence) runs OUT-of-band and presents the
/// extracted text as the <see cref="OcrExtractedJson"/> blob — today the field
/// is accepted opaquely so the wire contract is forward-compatible.
/// </remarks>
/// <param name="FileBase64">
/// Base64-encoded file bytes. Required; non-empty; well-formed base64; decoded
/// byte length ≤ <c>AttachmentOptions.MaxBytes</c>. The R0227
/// <c>IAttachmentValidator</c> enforces the magic-byte sniff downstream.
/// </param>
/// <param name="FileName">
/// Original filename as the user supplied it. 1..255 chars, must contain an
/// extension, no path separators. The attachment service sanitises this
/// further before persisting.
/// </param>
/// <param name="ContentType">
/// Optional MIME hint. When supplied it must match one of the configured
/// allow-list types in <c>AttachmentOptions.AllowedMimeTypes</c>; when null
/// the attachment service detects the MIME from the magic-byte sniff. Typical
/// values: <c>"application/pdf"</c>, <c>"image/jpeg"</c>, <c>"image/png"</c>.
/// </param>
/// <param name="OcrExtractedJson">
/// Optional JSON blob carrying the OCR-extracted fields. Capped at 100 000
/// chars by the validator. Null when no OCR took place (a scanned copy is
/// uploaded for paper-trail purposes without text extraction).
/// </param>
/// <param name="OcrConfidenceLevel">
/// Optional categorical confidence band; one of <c>"High"</c>, <c>"Medium"</c>,
/// <c>"Low"</c> (case-sensitive). The validator rejects values outside the
/// allow-list. Null when no OCR took place.
/// </param>
public sealed record ScannedDeclarationAttachmentInputDto(
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string FileBase64,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string FileName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ContentType = null,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string? OcrExtractedJson = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? OcrConfidenceLevel = null);

// ────────────────────────────────────────────────────────────────────────────
// R0822 / BP 1.2-M — Declarations registry explorer.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0822 / BP 1.2-M / Annex 1 §8.1.3 — input envelope for the Declarations
/// explorer endpoint. Carries an optional QBE filter (R0163) plus an explicit
/// reporting-window pair + paging slots.
/// </summary>
/// <remarks>
/// The explorer is paged + budget-gated: the service consults
/// <c>Cnas.Ps.Application.QueryBudget.IQueryBudgetService</c> with the
/// <c>"Declaration"</c> registry code BEFORE materialising. A too-broad call
/// (no payer / kind / QBE narrowing) returns
/// <c>Cnas.Ps.Core.Common.ErrorCodes.QueryTooBroad</c> so the UI can surface
/// a refinement prompt.
/// </remarks>
/// <param name="Filter">
/// Optional R0163 QBE wire envelope (<see cref="QbeFilterDto"/>) evaluated
/// against the <c>QueryBudgetRegistries.Declaration</c> schema (queryable
/// fields: <c>Id</c>, <c>ContributorId</c>, <c>Kind</c>,
/// <c>ReportingMonth</c>, <c>FiledAtUtc</c>, <c>Status</c>,
/// <c>ReferenceNumber</c>, <c>DeclaredContributionAmount</c>,
/// <c>RegisteredByOffice</c>, <c>FormVersion</c>, <c>HasScannedCopy</c>).
/// </param>
/// <param name="FromUtc">
/// Optional inclusive lower bound on
/// <c>Declaration.FiledAtUtc</c>. Independent of any QBE date filter — both
/// can be applied simultaneously.
/// </param>
/// <param name="ToUtc">
/// Optional exclusive upper bound on <c>Declaration.FiledAtUtc</c>.
/// </param>
/// <param name="Skip">Number of rows to skip; clamped to <c>≥ 0</c>.</param>
/// <param name="Take">
/// Page size; defaults to 50 and is capped at 200 by the validator.
/// </param>
public sealed record DeclarationsSearchInput(
    QbeFilterDto? Filter = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? FromUtc = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? ToUtc = null,
    int Skip = 0,
    int Take = 50);

/// <summary>
/// R0822 / BP 1.2-M / Annex 1 §8.1.3 — output envelope returned by the
/// Declarations explorer endpoint. Carries the page slice, the unclamped
/// total-row count, and a slot for any future refinement suggestions
/// (R0525 — currently empty).
/// </summary>
/// <param name="Items">The materialised page of <see cref="DeclarationDto"/> rows.</param>
/// <param name="TotalCount">
/// Total matching rows across the entire result set (NOT just the returned
/// page). Sourced from the budget-verdict count so a second COUNT round-trip
/// is avoided.
/// </param>
/// <param name="AppliedSuggestions">
/// R0525 — refinement suggestions surfaced by the suggestion service. Empty in
/// this build until the Declarations registry adopts the R0525 suggestion
/// pipeline.
/// </param>
public sealed record DeclarationsListPageDto(
    IReadOnlyList<DeclarationDto> Items,
    int TotalCount,
    IReadOnlyList<string> AppliedSuggestions);
