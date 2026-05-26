using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0910 / R0913 — REV-5 declarations (insured-person-level breakdown) and
// per-insured-person contribution adjustments from other documents.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0910 / BP 2.2-A — outbound DTO for a REV-5 declaration header. Aggregates
/// the per-employee rows registered for one (employer × month) tuple. The
/// <see cref="UnmatchedRowCount"/> + <see cref="UnmatchedNationalIdHashPrefixes"/>
/// surface signals partial-success registration paths where some IDNP hashes
/// could not be resolved to a known <c>Solicitant</c>.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying declaration row.</param>
/// <param name="FilingContributorSqid">Sqid-encoded id of the filing employer (Plătitor).</param>
/// <param name="ReportingMonth">Calendar month the declaration covers (day = 1).</param>
/// <param name="FiledAtUtc">UTC instant the declaration was filed.</param>
/// <param name="ReferenceNumber">External reference number assigned by the employer.</param>
/// <param name="Status">
/// Stable enum-name representation of the
/// <c>Cnas.Ps.Core.Domain.Rev5DeclarationStatus</c> value (<c>Received</c>,
/// <c>Validated</c>, <c>Adjusted</c>, <c>Cancelled</c>).
/// </param>
/// <param name="TotalDeclaredAmount">Sum of declared contributions across every active child row (MDL).</param>
/// <param name="RowCount">Number of child rows attached to the declaration.</param>
/// <param name="UnmatchedRowCount">
/// Number of child rows whose IDNP hash did not resolve to a known Solicitant
/// at registration time. The rows are still persisted, but no
/// <c>PersonalAccountEntry</c> was projected for them.
/// </param>
/// <param name="UnmatchedNationalIdHashPrefixes">
/// First 8 chars of the first 10 unmatched hashes (anti-enumeration —
/// operators can identify which records need a Solicitant registration
/// without exposing the full hash space).
/// </param>
/// <param name="Notes">Operator note attached to the declaration (≤ 500 chars when set).</param>
public sealed record Rev5DeclarationDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string FilingContributorSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly ReportingMonth,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime FiledAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ReferenceNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal TotalDeclaredAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int RowCount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int UnmatchedRowCount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string> UnmatchedNationalIdHashPrefixes,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes);

/// <summary>
/// R0910 / BP 2.2-A — single insured-person row inside a
/// <see cref="Rev5DeclarationRegisterInputDto"/>. Carries the IDNP hash
/// (never the plaintext) so the service can resolve the row to a Solicitant.
/// </summary>
/// <param name="InsuredPersonNationalIdHash">
/// Deterministic HMAC-SHA256 hash of the insured person's IDNP — must match
/// the hashing contract used by <c>Solicitant.NationalIdHash</c>.
/// </param>
/// <param name="ContributionBaseAmount">Gross salary subject to contribution (MDL).</param>
/// <param name="ContributionAmount">Contribution paid for this insured person (MDL).</param>
/// <param name="DaysWorked">Days worked in the reporting month (0..31 when set).</param>
/// <param name="PositionCode">Optional employer-assigned job-code (≤ 64 chars when set).</param>
public sealed record Rev5DeclarationRowInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string InsuredPersonNationalIdHash,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal ContributionBaseAmount,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal ContributionAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int? DaysWorked = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? PositionCode = null);

/// <summary>
/// R0910 / BP 2.2-A — input envelope for the
/// <c>POST /api/rev5-declarations</c> registration endpoint. Carries the
/// header attributes plus every child row.
/// </summary>
/// <param name="FilingContributorSqid">Sqid-encoded id of the filing employer (Plătitor).</param>
/// <param name="ReportingMonth">Calendar month the declaration covers (day = 1).</param>
/// <param name="ReferenceNumber">External reference (1..64 chars, required).</param>
/// <param name="Rows">Insured-person rows (1..50 000 entries enforced by the validator).</param>
/// <param name="Notes">Optional operator note (≤ 500 chars when supplied).</param>
/// <param name="FiledAtUtc">
/// Optional override of the filing instant. When <c>null</c> the service
/// uses the current UTC clock.
/// </param>
public sealed record Rev5DeclarationRegisterInputDto(
    string FilingContributorSqid,
    DateOnly ReportingMonth,
    string ReferenceNumber,
    IReadOnlyList<Rev5DeclarationRowInputDto> Rows,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes = null,
    DateTime? FiledAtUtc = null);

/// <summary>
/// R0910 / BP 2.2-A — outbound DTO for a single REV-5 child row. Exposes only
/// the first 8 characters of the IDNP hash as an anti-enumeration measure
/// (operators see enough to disambiguate but not enough to brute-force).
/// </summary>
/// <param name="Id">Sqid-encoded id of the row.</param>
/// <param name="NationalIdHashPrefix">First 8 chars of the IDNP hash (anti-enumeration).</param>
/// <param name="ContributionBaseAmount">Gross salary subject to contribution (MDL).</param>
/// <param name="ContributionAmount">Contribution paid for this insured person (MDL).</param>
/// <param name="DaysWorked">Days worked in the reporting month, when set.</param>
/// <param name="PositionCode">Employer-assigned job-code, when set.</param>
public sealed record Rev5DeclarationRowDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string NationalIdHashPrefix,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal ContributionBaseAmount,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal ContributionAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int? DaysWorked,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? PositionCode);

/// <summary>
/// R0910 / BP 2.2-A — input DTO for the per-row adjustment endpoint
/// <c>POST /api/rev5-declarations/{sqid}/rows/{nationalIdHash}/adjust</c>.
/// </summary>
/// <param name="AdjustedContributionAmount">New contribution amount (MDL, ≥ 0).</param>
/// <param name="Reason">Operator rationale (3..500 chars).</param>
public sealed record Rev5DeclarationRowAdjustInputDto(
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal AdjustedContributionAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);

/// <summary>
/// R0910 / BP 2.2-A — input DTO for the cancellation endpoint
/// <c>POST /api/rev5-declarations/{sqid}/cancel</c>.
/// </summary>
/// <param name="Reason">Operator cancellation rationale (3..500 chars).</param>
public sealed record Rev5DeclarationCancelInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);

/// <summary>
/// R0913 / BP 2.2-D — outbound DTO for a per-insured-person contribution
/// adjustment registered from a court decision, audit/control report,
/// individual social-insurance contract, or other supporting document.
/// </summary>
/// <param name="Id">Sqid-encoded id of the adjustment.</param>
/// <param name="InsuredPersonSolicitantSqid">Sqid-encoded id of the target Solicitant.</param>
/// <param name="Month">Calendar month the adjustment applies to (day = 1).</param>
/// <param name="AdjustmentAmount">Signed adjustment amount (MDL).</param>
/// <param name="SourceDocumentCode">Stable document-source code.</param>
/// <param name="SourceDocumentReference">Optional external reference, when set.</param>
/// <param name="Reason">Optional rationale, when set.</param>
public sealed record InsuredPersonContributionAdjustmentDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string InsuredPersonSolicitantSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly Month,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal AdjustmentAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string SourceDocumentCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? SourceDocumentReference,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Reason);

/// <summary>
/// R0913 / BP 2.2-D — input DTO for the
/// <c>POST /api/insured-person-adjustments</c> creation endpoint.
/// </summary>
/// <param name="InsuredPersonSolicitantSqid">Sqid-encoded id of the target Solicitant.</param>
/// <param name="Month">Calendar month the adjustment applies to (day = 1).</param>
/// <param name="AdjustmentAmount">Signed adjustment amount (MDL, |x| ≤ 10_000_000).</param>
/// <param name="SourceDocumentCode">
/// One of <c>"CourtDecision"</c>, <c>"AdminControl"</c>,
/// <c>"IndividualContract"</c>, <c>"Other"</c>.
/// </param>
/// <param name="SourceDocumentReference">Optional external reference (≤ 128 chars when set).</param>
/// <param name="Reason">Optional rationale (3..500 chars when set).</param>
public sealed record InsuredPersonContributionAdjustmentInputDto(
    string InsuredPersonSolicitantSqid,
    DateOnly Month,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal AdjustmentAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string SourceDocumentCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? SourceDocumentReference = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Reason = null);
