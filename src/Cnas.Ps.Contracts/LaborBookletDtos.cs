using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0920 / R0921 — Labor booklet (Carnet de muncă) + pre-1999 activity periods.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0920 / TOR BP 2.3-A — outbound projection of a <c>LaborBooklet</c> master
/// row. All identifiers are Sqid-encoded per CLAUDE.md RULE 3.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying booklet row.</param>
/// <param name="InsuredPersonSqid">Sqid-encoded id of the owning natural-person Solicitant.</param>
/// <param name="CarnetMuncaNumber">Booklet serial number typed verbatim from the paper original.</param>
/// <param name="IssuedDate">Issue date printed on the booklet cover, when legible.</param>
/// <param name="IssuingAuthority">Authority that issued the booklet (former employer, labour office, ...).</param>
/// <param name="Status">
/// Stable enum-name representation of the
/// <c>Cnas.Ps.Core.Domain.LaborBookletStatus</c> value (<c>Pending</c>,
/// <c>Verified</c>, <c>Rejected</c>).
/// </param>
/// <param name="OcrConfidenceLevel">
/// Categorical OCR-confidence band, one of <c>"High"</c>, <c>"Medium"</c>,
/// <c>"Low"</c>, or <see langword="null"/> when no OCR took place.
/// </param>
/// <param name="VerifierNotes">Verifier's free-text rationale captured at verification time.</param>
/// <param name="VerifiedByUserSqid">Sqid-encoded id of the operator that verified the booklet.</param>
/// <param name="VerifiedAtUtc">UTC instant the booklet was verified.</param>
/// <param name="RejectionReason">Rejection rationale captured at rejection time.</param>
/// <param name="RejectedAtUtc">UTC instant the booklet was rejected.</param>
/// <param name="HasScannedCopy">True when the row has at least one scanned copy attached.</param>
public sealed record LaborBookletDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string InsuredPersonSqid,
    [property: SensitivityClassification(SensitivityLabel.Restricted)]
    string CarnetMuncaNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? IssuedDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? IssuingAuthority,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? OcrConfidenceLevel,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? VerifierNotes,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? VerifiedByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? VerifiedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? RejectionReason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? RejectedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool HasScannedCopy);

/// <summary>
/// R0920 / TOR BP 2.3-A — input envelope for the
/// <c>POST /api/labor-booklets</c> endpoint. Creates a Pending master row;
/// the scanned binary is attached separately via the scanned-copy endpoint.
/// </summary>
/// <param name="InsuredPersonSqid">Sqid-encoded id of the owning natural-person Solicitant.</param>
/// <param name="CarnetMuncaNumber">Booklet serial number. 1..32 chars, <c>^[A-Z0-9-]+$</c>.</param>
/// <param name="IssuedDate">Optional issue date printed on the booklet cover.</param>
/// <param name="IssuingAuthority">Optional issuing-authority name (1..200 chars when supplied).</param>
/// <param name="Notes">Optional operator note captured at registration time (3..500 chars when supplied).</param>
public sealed record LaborBookletRegisterInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string InsuredPersonSqid,
    [property: SensitivityClassification(SensitivityLabel.Restricted)]
    string CarnetMuncaNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? IssuedDate = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? IssuingAuthority = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes = null);

/// <summary>
/// R0920 / TOR BP 2.3-A — input envelope for the
/// <c>POST /api/labor-booklets/{sqid}/verify</c> endpoint. Captures an
/// optional verifier note.
/// </summary>
/// <param name="Notes">Optional verifier rationale (3..500 chars when supplied).</param>
public sealed record LaborBookletVerifyInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes = null);

/// <summary>
/// R0920 / TOR BP 2.3-A — input envelope for the
/// <c>POST /api/labor-booklets/{sqid}/reject</c> endpoint. The reason is
/// required.
/// </summary>
/// <param name="Reason">Operator-supplied rejection rationale (3..500 chars).</param>
public sealed record LaborBookletRejectInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);

/// <summary>
/// R0920 / TOR BP 2.3-A — input envelope for the
/// <c>POST /api/labor-booklets/{sqid}/scanned-copy</c> endpoint. Carries the
/// caller-supplied bytes (base64-encoded), declared filename, and optional OCR
/// metadata pre-computed by the calling pipeline.
/// </summary>
/// <param name="FileBase64">Base64-encoded file bytes; non-empty; well-formed base64.</param>
/// <param name="FileName">Original filename (1..255 chars, no path separators).</param>
/// <param name="OcrExtractedJson">Optional JSON blob carrying OCR-extracted fields. ≤ 100 000 chars.</param>
/// <param name="OcrConfidenceLevel">
/// Optional categorical confidence band; one of <c>"High"</c>, <c>"Medium"</c>,
/// <c>"Low"</c> (case-sensitive).
/// </param>
public sealed record ScannedCopyAttachmentInputDto(
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string FileBase64,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string FileName,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string? OcrExtractedJson = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? OcrConfidenceLevel = null);

/// <summary>
/// R0921 / TOR BP 2.3-B — outbound projection of an
/// <c>InsuredPersonPre1999Period</c> row.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the period row.</param>
/// <param name="InsuredPersonSqid">Sqid-encoded id of the owning natural-person Solicitant.</param>
/// <param name="LaborBookletSqid">Sqid-encoded id of the sourcing booklet, when linked.</param>
/// <param name="PeriodStartDate">Inclusive start date of the activity period.</param>
/// <param name="PeriodEndDate">Inclusive end date (≤ 1998-12-31).</param>
/// <param name="EmployerName">Employer name as recorded in the booklet.</param>
/// <param name="Position">Position / job title as recorded in the booklet.</param>
/// <param name="DaysWorked">Optional days-worked figure (0..366).</param>
/// <param name="ProofDocumentReference">External reference to the supporting paper document.</param>
/// <param name="Notes">Free-text annotation.</param>
/// <param name="ValidFromUtc">UTC instant the row became active.</param>
/// <param name="ValidToUtc">UTC instant the row was superseded; null while current.</param>
/// <param name="ChangeReason">Operator-supplied rationale for the most recent change.</param>
public sealed record InsuredPersonPre1999PeriodDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string InsuredPersonSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? LaborBookletSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly PeriodStartDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly PeriodEndDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? EmployerName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Position,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int? DaysWorked,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ProofDocumentReference,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime ValidFromUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? ValidToUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ChangeReason);

/// <summary>
/// R0921 / TOR BP 2.3-B — input envelope for the add / amend period endpoints.
/// </summary>
/// <param name="PeriodStartDate">Inclusive start date of the activity period.</param>
/// <param name="PeriodEndDate">
/// Inclusive end date. Validator enforces <c>&lt;= 1998-12-31</c> at the
/// boundary.
/// </param>
/// <param name="EmployerName">Employer name (1..200 chars when supplied).</param>
/// <param name="Position">Position / job title (1..200 chars when supplied).</param>
/// <param name="DaysWorked">Optional days-worked figure (0..366 when supplied).</param>
/// <param name="ProofDocumentReference">External reference to the supporting paper document (≤ 200 chars).</param>
/// <param name="Notes">Free-text annotation (3..500 chars when supplied).</param>
/// <param name="ChangeReason">Operator-supplied rationale (3..500 chars when supplied).</param>
public sealed record InsuredPersonPre1999PeriodInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly PeriodStartDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly PeriodEndDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? EmployerName = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Position = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int? DaysWorked = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ProofDocumentReference = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ChangeReason = null);

/// <summary>
/// R0922 / TOR Annex 2 §8.2.4 — outbound projection of one pre-1999 stagiu
/// Years/Months/Days roll-up row attached directly to an InsuredPerson.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the stagiu record.</param>
/// <param name="InsuredPersonSqid">Sqid-encoded id of the owning InsuredPerson.</param>
/// <param name="FromDate">Inclusive start date of the period (must be pre-1999).</param>
/// <param name="ToDate">Inclusive end date (must be pre-1999).</param>
/// <param name="Years">Whole-year tally of the period (0..70).</param>
/// <param name="Months">Whole-month tally (0..11).</param>
/// <param name="Days">Whole-day tally (0..30).</param>
/// <param name="Source">Free-text source attribution per Annex 2 note.</param>
/// <param name="Notes">Free-text annotation.</param>
public sealed record Pre1999StagiuDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string InsuredPersonSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly FromDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly ToDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Years,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Months,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Days,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Source,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes);

/// <summary>
/// R0922 / TOR Annex 2 §8.2.4 — input envelope for appending a pre-1999 stagiu
/// roll-up row to an InsuredPerson aggregate.
/// </summary>
/// <param name="FromDate">Inclusive start date — must be pre-1999.</param>
/// <param name="ToDate">Inclusive end date — must be pre-1999.</param>
/// <param name="Years">Whole-year component (0..70).</param>
/// <param name="Months">Whole-month component (0..11).</param>
/// <param name="Days">Whole-day component (0..30).</param>
/// <param name="Source">Optional source attribution (≤ 200 chars).</param>
/// <param name="Notes">Optional free-text annotation (≤ 500 chars).</param>
public sealed record Pre1999StagiuInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly FromDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly ToDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Years,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Months,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Days,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Source = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes = null);
