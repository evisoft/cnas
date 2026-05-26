using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R1201 / R1402 / TOR §3.4-B / §3.6-C — International-agreements (3-level
// routing) wire DTOs. The aggregate is reusable across two benefit kinds:
// IncapacityMaternity (R1201) and Unemployment (R1402). The same wire
// shape backs both — per-benefit-kind routing policies discriminate by
// the BenefitKind field.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R1201 / R1402 — one international-agreements review case as it leaves the
/// system. The raw IDNP is never returned — only the deterministic HMAC
/// hash (44 chars base64) is surfaced so external consumers can correlate
/// but cannot reconstruct the plaintext identifier.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying row.</param>
/// <param name="CaseNumber">Stable external identifier (e.g. <c>IAR-2026-000001</c>).</param>
/// <param name="BenefitKind">Stable enum-name of the benefit-kind discriminator.</param>
/// <param name="BeneficiaryIdnpHash">HMAC-SHA256 base64 hash of the beneficiary IDNP — opaque external pointer.</param>
/// <param name="BeneficiaryDisplayName">Display name of the beneficiary (3..256 chars).</param>
/// <param name="AgreementCode">Stable bilateral-agreement code (e.g. <c>RO_MD_2006</c>).</param>
/// <param name="HostCountryCode">ISO-3166 alpha-2 uppercase host-country code.</param>
/// <param name="Status">Stable enum-name of the lifecycle status.</param>
/// <param name="CurrentLevel">Stable enum-name of the routing level the case is sitting at.</param>
/// <param name="ReferenceBenefitPassportSqid">Optional Sqid pointer to the parent benefit passport.</param>
/// <param name="SubmittedAt">UTC timestamp the case entered the routing chain, when applicable.</param>
/// <param name="ApprovedAt">UTC timestamp the case was finally approved, when applicable.</param>
/// <param name="RejectedAt">UTC timestamp the case was rejected, when applicable.</param>
/// <param name="RejectionReason">Reviewer-supplied rejection rationale, when applicable.</param>
/// <param name="RevisionRequestedAt">UTC timestamp the case entered RevisionRequested, when applicable.</param>
/// <param name="RevisionRequestNote">Reviewer-supplied revision-request note, when applicable.</param>
/// <param name="CancelledAt">UTC timestamp the case was cancelled, when applicable.</param>
/// <param name="CancelReason">Operator-supplied cancellation rationale, when applicable.</param>
/// <param name="EvidenceJson">JSON-encoded supporting-evidence envelope (no PII).</param>
/// <param name="RegisteredAt">UTC timestamp the row was created (mirror of <c>CreatedAtUtc</c>).</param>
/// <param name="Steps">Append-only review-step history ordered by review timestamp.</param>
public sealed record IntlAgreementReviewCaseDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string CaseNumber,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string BenefitKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string BeneficiaryIdnpHash,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string BeneficiaryDisplayName,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string AgreementCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string HostCountryCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string CurrentLevel,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? ReferenceBenefitPassportSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? SubmittedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? ApprovedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? RejectedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? RejectionReason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? RevisionRequestedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? RevisionRequestNote,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? CancelledAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CancelReason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? EvidenceJson,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime RegisteredAt,
    IReadOnlyList<IntlAgreementReviewStepDto> Steps);

/// <summary>
/// R1201 / R1402 — one review-level decision row attached to an
/// international-agreements review case.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying row.</param>
/// <param name="CaseSqid">Sqid-encoded id of the parent case.</param>
/// <param name="Level">Stable enum-name of the routing level at which the decision was made.</param>
/// <param name="Outcome">Stable enum-name of the reviewer decision.</param>
/// <param name="ReviewedAt">UTC timestamp the reviewer recorded the decision.</param>
/// <param name="ReviewedByUserSqid">Sqid-encoded id of the reviewer (when known).</param>
/// <param name="Note">Reviewer-supplied note (3..2000 chars; no PII).</param>
public sealed record IntlAgreementReviewStepDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string CaseSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Level,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Outcome,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime ReviewedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ReviewedByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Note);

/// <summary>
/// R1201 / R1402 — input envelope for <c>POST /api/intl-agreement-cases</c>.
/// Accepts the raw IDNP at the boundary; the service layer encrypts +
/// hashes it before persistence.
/// </summary>
/// <param name="BenefitKind">Stable enum-name of the benefit-kind discriminator.</param>
/// <param name="BeneficiaryIdnp">Beneficiary IDNP (13 digits).</param>
/// <param name="BeneficiaryDisplayName">Display name of the beneficiary (3..256 chars).</param>
/// <param name="AgreementCode">Stable bilateral-agreement code matching <c>^[A-Z]{2}_MD_\d{4}$</c>.</param>
/// <param name="HostCountryCode">ISO-3166 alpha-2 uppercase code (exactly 2 chars).</param>
/// <param name="ReferenceBenefitPassportSqid">Optional Sqid pointer to the parent benefit passport.</param>
/// <param name="EvidenceJson">Optional JSON-encoded supporting-evidence envelope (≤ 16 384 chars).</param>
public sealed record IntlAgreementReviewCaseCreateInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string BenefitKind,
    [property: SensitivityClassification(SensitivityLabel.Restricted)]
    string BeneficiaryIdnp,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string BeneficiaryDisplayName,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string AgreementCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string HostCountryCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? ReferenceBenefitPassportSqid = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? EvidenceJson = null);

/// <summary>
/// R1201 / R1402 — input envelope for
/// <c>POST /api/intl-agreement-cases/{sqid}/review</c>. Carries the
/// reviewer's decision at the current routing level plus a mandatory note.
/// </summary>
/// <param name="Outcome">Stable enum-name of the reviewer decision (Approved / Rejected / RevisionRequested).</param>
/// <param name="Note">Reviewer-supplied note (3..2000 chars).</param>
public sealed record IntlAgreementReviewInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Outcome,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Note);

/// <summary>
/// R1201 / R1402 — input envelope for
/// <c>POST /api/intl-agreement-cases/{sqid}/resubmit</c>. Carries the
/// updated evidence + a re-submit note; the case re-enters the chain from
/// level 1.
/// </summary>
/// <param name="Note">Operator-supplied re-submit note (3..2000 chars).</param>
/// <param name="EvidenceJson">Optional updated evidence JSON (≤ 16 384 chars).</param>
public sealed record IntlAgreementReviewCaseResubmitInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Note,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? EvidenceJson = null);

/// <summary>
/// R1201 / R1402 — input envelope for
/// <c>POST /api/intl-agreement-cases/{sqid}/cancel</c> and any other
/// reason-only transition. Carries the operator-supplied rationale.
/// </summary>
/// <param name="Reason">Operator-supplied rationale (3..2000 chars).</param>
public sealed record IntlAgreementReviewCaseReasonInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);

/// <summary>
/// R1201 / R1402 — filter envelope for the list endpoint.
/// </summary>
/// <param name="Status">Optional lifecycle-status filter — null returns all statuses.</param>
/// <param name="BenefitKind">Optional benefit-kind filter — null returns all kinds.</param>
/// <param name="AgreementCode">Optional agreement-code filter — null returns all agreements.</param>
/// <param name="HostCountryCode">Optional host-country-code filter — null returns all countries.</param>
/// <param name="CurrentLevel">Optional current-level filter — null returns all levels.</param>
/// <param name="Skip">Page offset (≥ 0).</param>
/// <param name="Take">Page size (1..100).</param>
public sealed record IntlAgreementReviewCaseFilterDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Status = null,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? BenefitKind = null,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? AgreementCode = null,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? HostCountryCode = null,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? CurrentLevel = null,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Skip = 0,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Take = 25);

/// <summary>
/// R1201 / R1402 — page envelope returned by the list endpoint.
/// </summary>
/// <param name="Items">Rows on the current page.</param>
/// <param name="Total">Total matching row count across all pages.</param>
/// <param name="Skip">Page offset echoed back to the caller.</param>
/// <param name="Take">Page size echoed back to the caller.</param>
public sealed record IntlAgreementReviewCasePageDto(
    IReadOnlyList<IntlAgreementReviewCaseDto> Items,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Total,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Take);
