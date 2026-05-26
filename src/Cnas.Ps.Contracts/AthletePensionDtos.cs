using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R1403 / TOR §3.6-D — Athlete pensions (indemnizație viageră sportivi
// performanță + antrenori)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R1403 — one athlete-pension award as it leaves the system. The raw IDNP is
/// never returned — only the deterministic HMAC hash (44 chars base64) is
/// surfaced so external consumers can correlate but cannot reconstruct the
/// plaintext identifier.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying row.</param>
/// <param name="AwardNumber">Stable external identifier (e.g. <c>APE-2026-000001</c>).</param>
/// <param name="BeneficiaryIdnpHash">HMAC-SHA256 base64 hash of the beneficiary IDNP — opaque external pointer.</param>
/// <param name="BeneficiaryDisplayName">Display name of the beneficiary (3..256 chars).</param>
/// <param name="BeneficiaryBirthDate">Beneficiary date of birth.</param>
/// <param name="BeneficiarySex">Stable enum-name of biological sex.</param>
/// <param name="Role">Stable enum-name of the award role (<c>Athlete</c> / <c>Coach</c>).</param>
/// <param name="SportDiscipline">Sport-discipline code (e.g. <c>ATHLETICS</c>).</param>
/// <param name="Status">Stable enum-name of the lifecycle status.</param>
/// <param name="RequestedAt">UTC timestamp the request was created.</param>
/// <param name="ApprovedAt">UTC timestamp the award was approved, when applicable.</param>
/// <param name="RejectedAt">UTC timestamp the award was rejected, when applicable.</param>
/// <param name="RejectionReason">Operator-supplied rejection rationale, when applicable.</param>
/// <param name="EffectiveFrom">Calendar date from which the monthly pension starts to accrue.</param>
/// <param name="SuspendedAt">UTC timestamp the award was suspended, when applicable.</param>
/// <param name="SuspensionReason">Operator-supplied suspension rationale, when applicable.</param>
/// <param name="TerminatedAt">UTC timestamp the award was terminated, when applicable.</param>
/// <param name="TerminationReason">Operator-supplied termination rationale, when applicable.</param>
/// <param name="MonthlyAmountMdl">Monthly pension amount in MDL (0 until Approved).</param>
/// <param name="RegulatoryBaseMdl">Snapshot of the regulatory base amount used at approval.</param>
/// <param name="MultiplierPercent">Snapshot of the final multiplier (percent).</param>
/// <param name="EligibilityNotesJson">JSON-encoded eligibility reasoning trace (no PII).</param>
/// <param name="RegisteredAt">UTC timestamp the row was created (mirror of <c>CreatedAtUtc</c>).</param>
/// <param name="LastRecomputedAt">UTC timestamp of the last amount recomputation, when applicable.</param>
/// <param name="CareerRecords">All verified + unverified career-record rows attached to the award.</param>
public sealed record AthletePensionAwardDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string AwardNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string BeneficiaryIdnpHash,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string BeneficiaryDisplayName,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    DateOnly BeneficiaryBirthDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string BeneficiarySex,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Role,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string SportDiscipline,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime RequestedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? ApprovedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? RejectedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? RejectionReason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? EffectiveFrom,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? SuspendedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? SuspensionReason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? TerminatedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? TerminationReason,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal MonthlyAmountMdl,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal RegulatoryBaseMdl,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    decimal MultiplierPercent,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? EligibilityNotesJson,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime RegisteredAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? LastRecomputedAt,
    IReadOnlyList<AthleteCareerRecordDto> CareerRecords);

/// <summary>
/// R1403 — one verified-or-pending career-record row attached to an
/// <see cref="AthletePensionAwardDto"/>.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying row.</param>
/// <param name="AwardSqid">Sqid-encoded id of the parent award.</param>
/// <param name="AchievementKind">Stable enum-name of the achievement kind.</param>
/// <param name="AchievementYear">Calendar year in which the achievement occurred.</param>
/// <param name="Event">Event name / discipline detail.</param>
/// <param name="Years">Coach years-of-service (only populated for <c>CoachYearsService</c>).</param>
/// <param name="Verified">True once an operator has confirmed the supporting evidence.</param>
/// <param name="VerifiedAt">UTC timestamp of verification, when applicable.</param>
/// <param name="VerificationNote">Operator-supplied verification note, when applicable.</param>
/// <param name="EvidenceDocumentReference">Opaque reference to the evidence document.</param>
public sealed record AthleteCareerRecordDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string AwardSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string AchievementKind,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int AchievementYear,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Event,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int? Years,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool Verified,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? VerifiedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? VerificationNote,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? EvidenceDocumentReference);

/// <summary>
/// R1403 — input envelope for <c>POST /api/athlete-pensions</c>. Accepts the
/// raw IDNP at the boundary; the service layer encrypts + hashes it before
/// persistence.
/// </summary>
/// <param name="BeneficiaryIdnp">Beneficiary IDNP (13 digits). Encrypted at rest by the service layer.</param>
/// <param name="BeneficiaryDisplayName">Display name of the beneficiary (3..256 chars).</param>
/// <param name="BeneficiaryBirthDate">Beneficiary date of birth — must be in the past.</param>
/// <param name="BeneficiarySex">Stable enum-name of biological sex.</param>
/// <param name="Role">Stable enum-name of the award role.</param>
/// <param name="SportDiscipline">Sport-discipline code (regex <c>^[A-Z][A-Z0-9_]{1,127}$</c>).</param>
public sealed record AthletePensionAwardCreateInputDto(
    [property: SensitivityClassification(SensitivityLabel.Restricted)]
    string BeneficiaryIdnp,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string BeneficiaryDisplayName,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    DateOnly BeneficiaryBirthDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string BeneficiarySex,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Role,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string SportDiscipline);

/// <summary>
/// R1403 — input envelope for adding a career-record row to an
/// in-progress award.
/// </summary>
/// <param name="AchievementKind">Stable enum-name of the achievement kind.</param>
/// <param name="AchievementYear">Calendar year of the achievement (1900..currentYear).</param>
/// <param name="Event">Event name / discipline detail (3..256 chars).</param>
/// <param name="Years">Required only when <paramref name="AchievementKind"/> is <c>CoachYearsService</c> (1..80).</param>
/// <param name="EvidenceDocumentReference">Optional opaque reference to supporting evidence.</param>
public sealed record AthleteCareerRecordInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string AchievementKind,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int AchievementYear,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Event,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int? Years,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? EvidenceDocumentReference);

/// <summary>
/// R1403 — verification envelope posted when an operator confirms the
/// supporting evidence for a career-record row.
/// </summary>
/// <param name="VerificationNote">Operator-supplied verification note (3..1000 chars).</param>
public sealed record AthleteCareerRecordVerificationInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string VerificationNote);

/// <summary>
/// R1403 — approval envelope posted when the award is approved. Carries the
/// regulatory base amount snapshotted on the row plus an optional list of
/// admin-supplied additional multipliers (each in [0.5, 3.0]).
/// </summary>
/// <param name="Note">Approver-supplied justification (3..1000 chars).</param>
/// <param name="RegulatoryBaseMdl">Regulatory base amount in MDL (> 0, ≤ 100_000_000).</param>
/// <param name="AdditionalMultipliers">Optional list of multiplicative adjustments (each in [0.5, 3.0]).</param>
public sealed record AthletePensionApprovalInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Note,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal RegulatoryBaseMdl,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    IReadOnlyList<decimal>? AdditionalMultipliers);

/// <summary>
/// R1403 — activation envelope posted when an approved award is activated for
/// payment.
/// </summary>
/// <param name="EffectiveFrom">Calendar date from which the monthly pension accrues (≥ today).</param>
/// <param name="Note">Operator-supplied activation note (3..1000 chars).</param>
public sealed record AthletePensionActivationInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly EffectiveFrom,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Note);

/// <summary>
/// R1403 — reason envelope used by reject / suspend / resume / terminate
/// endpoints. Carries the operator-supplied rationale (3..1000 chars).
/// </summary>
/// <param name="Reason">Operator-supplied rationale (3..1000 chars).</param>
public sealed record AthletePensionReasonInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);

/// <summary>
/// R1403 — filter envelope for the list endpoint.
/// </summary>
/// <param name="Status">Optional status filter — null returns all statuses.</param>
/// <param name="Role">Optional role filter — null returns all roles.</param>
/// <param name="SportDiscipline">Optional sport-discipline filter — null returns all disciplines.</param>
/// <param name="BeneficiaryIdnpHash">Optional beneficiary-hash filter — null returns all rows.</param>
/// <param name="Skip">Page offset (≥ 0).</param>
/// <param name="Take">Page size (1..100).</param>
public sealed record AthletePensionAwardFilterDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Status = null,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Role = null,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? SportDiscipline = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? BeneficiaryIdnpHash = null,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Skip = 0,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Take = 25);

/// <summary>
/// R1403 — page envelope returned by the list endpoint.
/// </summary>
/// <param name="Items">Rows on the current page.</param>
/// <param name="Total">Total matching row count across all pages.</param>
/// <param name="Skip">Page offset echoed back to the caller.</param>
/// <param name="Take">Page size echoed back to the caller.</param>
public sealed record AthletePensionAwardPageDto(
    IReadOnlyList<AthletePensionAwardDto> Items,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Total,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Take);

/// <summary>
/// R1403 — pure-function input to the eligibility evaluator. Exposes only
/// enum-name codes + dates; no PII flows into the evaluator.
/// </summary>
/// <param name="Role">Stable enum-name of the award role.</param>
/// <param name="BirthDate">Beneficiary date of birth.</param>
/// <param name="EvaluationDate">Calendar date at which eligibility is evaluated.</param>
/// <param name="VerifiedRecords">Verified achievement records contributing to eligibility.</param>
public sealed record AthletePensionEligibilityInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Role,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    DateOnly BirthDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly EvaluationDate,
    IReadOnlyList<EligibilityRecordDto> VerifiedRecords);

/// <summary>
/// R1403 — minimal contribution record fed to the eligibility evaluator and
/// the amount calculator. No PII; only enum-name codes + numeric fields.
/// </summary>
/// <param name="AchievementKind">Stable enum-name of the achievement kind.</param>
/// <param name="AchievementYear">Calendar year of the achievement.</param>
/// <param name="Years">Coach years-of-service (only for <c>CoachYearsService</c>).</param>
public sealed record EligibilityRecordDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string AchievementKind,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int AchievementYear,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int? Years);

/// <summary>
/// R1403 — eligibility verdict returned by the evaluator. <c>RuleHits</c> is
/// an explain trace consumed by the audit / reasoning UI.
/// </summary>
/// <param name="IsEligible">True when the beneficiary meets the role's eligibility threshold.</param>
/// <param name="Reason">Human-readable summary (PLACEHOLDER pending regulatory load).</param>
/// <param name="RuleHits">Per-rule explain trace.</param>
public sealed record AthletePensionEligibilityVerdictDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsEligible,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Reason,
    IReadOnlyList<EligibilityRuleHitDto> RuleHits);

/// <summary>
/// R1403 — single rule-hit row in the eligibility-evaluator explain trace.
/// Carries no PII — only stable rule codes + enum-name codes + numerics.
/// </summary>
/// <param name="RuleCode">Stable rule identifier (e.g. <c>R_ATHLETE.OLYMPIC_MEDAL</c>).</param>
/// <param name="AchievementKind">Optional achievement-kind enum-name code associated with the hit.</param>
/// <param name="Year">Optional calendar year associated with the hit.</param>
/// <param name="Points">Score contribution of the rule (for reporting; no business meaning yet).</param>
public sealed record EligibilityRuleHitDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string RuleCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? AchievementKind,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int? Year,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    decimal Points);

/// <summary>
/// R1403 — pure-function input to the amount calculator. Carries no PII —
/// only role + verified records + regulatory inputs.
/// </summary>
/// <param name="Role">Stable enum-name of the award role.</param>
/// <param name="VerifiedRecords">Verified achievement records contributing to the multiplier.</param>
/// <param name="RegulatoryBaseMdl">Regulatory base amount in MDL.</param>
/// <param name="AdditionalMultipliers">Optional admin-supplied multiplicative adjustments.</param>
public sealed record AthletePensionAmountInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Role,
    IReadOnlyList<EligibilityRecordDto> VerifiedRecords,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal RegulatoryBaseMdl,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    IReadOnlyList<decimal>? AdditionalMultipliers);

/// <summary>
/// R1403 — amount-calculator output.
/// </summary>
/// <param name="MonthlyAmountMdl">Computed monthly pension (MDL, 2 decimals, banker's rounding).</param>
/// <param name="RegulatoryBaseMdl">Echoed regulatory base amount used at computation time.</param>
/// <param name="BaseMultiplierPercent">Multiplier from medal tier + record additive (percent).</param>
/// <param name="FinalMultiplierPercent">Final multiplier after coach factor + additional multipliers.</param>
/// <param name="BreakdownJson">JSON-encoded per-record contribution trace (no PII).</param>
public sealed record AthletePensionAmountComputationDto(
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal MonthlyAmountMdl,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal RegulatoryBaseMdl,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    decimal BaseMultiplierPercent,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    decimal FinalMultiplierPercent,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string BreakdownJson);
