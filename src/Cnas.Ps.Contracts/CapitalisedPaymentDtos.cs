using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R1202 / TOR §3.4-C — Capitalised periodic payments (liquidation cases)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R1202 — one capitalised-payment request as it leaves the system. The raw
/// IDNP / IDNO are never returned — only the deterministic HMAC hash (44 chars
/// base64) is surfaced so external consumers can correlate but cannot
/// reconstruct the plaintext identifier.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying row.</param>
/// <param name="RequestNumber">Stable external identifier (e.g. <c>CPR-2026-000001</c>).</param>
/// <param name="BeneficiaryIdnpHash">HMAC-SHA256 base64 hash of the beneficiary IDNP — opaque external pointer.</param>
/// <param name="BeneficiaryBirthDate">Beneficiary date of birth (calendar date).</param>
/// <param name="BeneficiarySex">Beneficiary biological sex — stable enum-name (<c>Male</c>, <c>Female</c>).</param>
/// <param name="LiquidatedDebtorIdnoHash">HMAC-SHA256 base64 hash of the liquidated-debtor IDNO.</param>
/// <param name="LiquidatedDebtorName">Display name of the liquidated debtor.</param>
/// <param name="Status">Stable enum-name of <c>CapitalisedPaymentRequestStatus</c>.</param>
/// <param name="ObligationKind">Stable enum-name of <c>CapitalisedPaymentObligationKind</c>.</param>
/// <param name="MonthlyAmountMdl">Monthly indemnity amount being capitalised (MDL).</param>
/// <param name="ObligationStartDate">First month the periodic payment ran.</param>
/// <param name="ObligationEndDate">Last month the periodic payment must run; null = lifetime.</param>
/// <param name="ValuationDate">Date as of which the present value is computed.</param>
/// <param name="LegalDiscountRatePercent">Annual discount rate (%) used in the computation.</param>
/// <param name="RegisteredAt">UTC timestamp the request was created (mirrors <c>CreatedAtUtc</c>).</param>
/// <param name="CancellationReason">Operator-supplied rationale when status is <c>Cancelled</c>; null otherwise.</param>
public sealed record CapitalisedPaymentRequestDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RequestNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string BeneficiaryIdnpHash,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    DateOnly BeneficiaryBirthDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string BeneficiarySex,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string LiquidatedDebtorIdnoHash,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string LiquidatedDebtorName,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ObligationKind,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal MonthlyAmountMdl,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly ObligationStartDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? ObligationEndDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly ValuationDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal LegalDiscountRatePercent,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime RegisteredAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CancellationReason);

/// <summary>
/// R1202 — input envelope for <c>POST /api/capitalised-payments</c>. Accepts
/// raw IDNP / IDNO at the boundary; the service hashes + encrypts them before
/// persistence and subsequent reads never return the plaintext.
/// </summary>
/// <param name="BeneficiaryIdnp">Beneficiary IDNP (13 digits). Encrypted at rest by the service layer.</param>
/// <param name="BeneficiaryBirthDate">Beneficiary date of birth — must be in the past.</param>
/// <param name="BeneficiarySex">Stable enum-name of <c>BeneficiarySex</c>.</param>
/// <param name="LiquidatedDebtorIdno">Liquidated-debtor IDNO (13 digits).</param>
/// <param name="LiquidatedDebtorName">Display name of the liquidated debtor (3..256 chars).</param>
/// <param name="ObligationKind">Stable enum-name of <c>CapitalisedPaymentObligationKind</c>.</param>
/// <param name="MonthlyAmountMdl">Monthly indemnity amount; <c>(0, 100_000_000]</c>; 2 decimals.</param>
/// <param name="ObligationStartDate">First month the periodic payment ran.</param>
/// <param name="ObligationEndDate">Last month the periodic payment must run; null = lifetime.</param>
/// <param name="ValuationDate">Date as of which the present value will be computed; <c>[today-7d, today+365d]</c>.</param>
/// <param name="LegalDiscountRatePercent">Annual discount rate (%); <c>[0, 30]</c>; 4 decimals.</param>
public sealed record CapitalisedPaymentRequestCreateInputDto(
    [property: SensitivityClassification(SensitivityLabel.Restricted)]
    string BeneficiaryIdnp,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    DateOnly BeneficiaryBirthDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string BeneficiarySex,
    [property: SensitivityClassification(SensitivityLabel.Restricted)]
    string LiquidatedDebtorIdno,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string LiquidatedDebtorName,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ObligationKind,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal MonthlyAmountMdl,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly ObligationStartDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? ObligationEndDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly ValuationDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal LegalDiscountRatePercent);

/// <summary>
/// R1202 — input envelope for <c>PUT /api/capitalised-payments/{sqid}</c>.
/// Nullable fields = "leave unchanged". <paramref name="ChangeReason"/> is
/// mandatory (3..1000 chars).
/// </summary>
/// <param name="BeneficiaryBirthDate">New birth date; null leaves unchanged.</param>
/// <param name="BeneficiarySex">New sex (stable enum-name); null leaves unchanged.</param>
/// <param name="LiquidatedDebtorName">New debtor name; null leaves unchanged.</param>
/// <param name="ObligationKind">New obligation kind; null leaves unchanged.</param>
/// <param name="MonthlyAmountMdl">New monthly amount; null leaves unchanged.</param>
/// <param name="ObligationStartDate">New obligation start; null leaves unchanged.</param>
/// <param name="ObligationEndDate">New obligation end; null leaves unchanged (use empty payload to set lifetime — handled at the service layer).</param>
/// <param name="ValuationDate">New valuation date; null leaves unchanged.</param>
/// <param name="LegalDiscountRatePercent">New discount rate; null leaves unchanged.</param>
/// <param name="ChangeReason">Mandatory operator-supplied rationale (3..1000 chars).</param>
public sealed record CapitalisedPaymentRequestModifyInputDto(
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    DateOnly? BeneficiaryBirthDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string? BeneficiarySex,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? LiquidatedDebtorName,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? ObligationKind,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? MonthlyAmountMdl,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? ObligationStartDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? ObligationEndDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? ValuationDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? LegalDiscountRatePercent,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ChangeReason);

/// <summary>
/// R1202 — reason envelope used by reject + cancel endpoints. Carries the
/// operator-supplied rationale (3..1000 chars).
/// </summary>
/// <param name="Reason">Operator-supplied rationale (3..1000 chars).</param>
public sealed record CapitalisedPaymentReasonInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);

/// <summary>
/// R1202 — approval envelope posted by the operator that approves a computed
/// decision. The mandatory note ensures every approval carries a human-
/// readable justification on the audit trail.
/// </summary>
/// <param name="Note">Approver-supplied justification (3..1000 chars).</param>
public sealed record CapitalisedPaymentApprovalInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Note);

/// <summary>
/// R1202 — settlement envelope posted by the operator that records a
/// liquidator-paid treasury receipt against an Approved request, transitioning
/// it to <c>Settled</c>.
/// </summary>
/// <param name="TreasuryReceiptSqid">Sqid-encoded id of the treasury-receipt row.</param>
/// <param name="SettlementNote">Operator-supplied settlement note (3..1000 chars).</param>
public sealed record CapitalisedPaymentSettlementInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string TreasuryReceiptSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string SettlementNote);

/// <summary>
/// R1202 — filter envelope for the list endpoint.
/// </summary>
/// <param name="Status">Optional stable enum-name filter — null returns all statuses.</param>
/// <param name="ObligationKind">Optional obligation-kind filter — null returns all kinds.</param>
/// <param name="BeneficiaryIdnpHash">Optional beneficiary-hash filter — null returns all rows.</param>
/// <param name="Skip">Page offset; ≥ 0.</param>
/// <param name="Take">Page size; 1..100.</param>
public sealed record CapitalisedPaymentRequestFilterDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Status = null,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? ObligationKind = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? BeneficiaryIdnpHash = null,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Skip = 0,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Take = 25);

/// <summary>
/// R1202 — page envelope returned by the list endpoint.
/// </summary>
/// <param name="Items">Rows on the current page.</param>
/// <param name="Total">Total matching row count across all pages.</param>
/// <param name="Skip">Page offset echoed back to the caller.</param>
/// <param name="Take">Page size echoed back to the caller.</param>
public sealed record CapitalisedPaymentRequestPageDto(
    IReadOnlyList<CapitalisedPaymentRequestDto> Items,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Total,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Take);

/// <summary>
/// R1202 — projection of one finalised <c>CapitalisedPaymentDecision</c> row
/// as it leaves the system.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying decision row.</param>
/// <param name="RequestSqid">Sqid-encoded id of the parent request.</param>
/// <param name="DecisionStatus">Stable enum-name of the decision outcome (<c>Approved</c> / <c>Rejected</c>).</param>
/// <param name="ComputedAtUtc">UTC instant the computation ran.</param>
/// <param name="EffectiveAgeYears">Beneficiary age at the valuation date (fractional, 2 decimals).</param>
/// <param name="LifeExpectancyMonths">Number of monthly periods covered by the computation.</param>
/// <param name="EffectiveDiscountMonthly">Monthly compounded discount factor (8 decimals).</param>
/// <param name="CapitalisedAmountMdl">Computed present value (MDL).</param>
/// <param name="ComputationBreakdownJson">JSON-encoded per-period breakdown (≤ 32 KiB).</param>
/// <param name="ApprovedByUserSqid">Sqid-encoded id of the approving user, when applicable.</param>
/// <param name="RejectionReason">Operator-supplied rejection reason, when applicable.</param>
public sealed record CapitalisedPaymentDecisionDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string RequestSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DecisionStatus,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime ComputedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal EffectiveAgeYears,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int LifeExpectancyMonths,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal EffectiveDiscountMonthly,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal CapitalisedAmountMdl,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string ComputationBreakdownJson,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? ApprovedByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? RejectionReason);

/// <summary>
/// R1202 — pure-function input to <c>IPresentValueAnnuityCalculator.Compute</c>.
/// Exposed as a Contracts type only so the calculator's tests live next to
/// the rest of the testing infrastructure; the DTO is internal-use and is
/// NOT bound to any REST input shape.
/// </summary>
/// <param name="BeneficiarySex">Beneficiary biological sex driving the mortality-table lookup.</param>
/// <param name="AgeAtValuationYears">Fractional beneficiary age at the valuation date (e.g. 47.25).</param>
/// <param name="MonthlyAmountMdl">Monthly indemnity amount being capitalised (MDL).</param>
/// <param name="ValuationDate">Date as of which the present value is computed.</param>
/// <param name="ObligationEndDate">Optional fixed obligation end; null = lifetime.</param>
/// <param name="AnnualDiscountRatePercent">Annual legal discount rate (%); <c>[0, 30]</c>.</param>
public sealed record CapitalisedAnnuityInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string BeneficiarySex,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal AgeAtValuationYears,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal MonthlyAmountMdl,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly ValuationDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? ObligationEndDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal AnnualDiscountRatePercent);

/// <summary>
/// R1202 — computation output produced by
/// <c>IPresentValueAnnuityCalculator.Compute</c>. Exposed for testing; the
/// service layer maps this to a persisted <c>CapitalisedPaymentDecision</c>
/// row and a <see cref="CapitalisedPaymentDecisionDto"/> on the wire.
/// </summary>
/// <param name="CapitalisedAmountMdl">Computed present value (MDL, 2 decimals).</param>
/// <param name="LifeExpectancyMonths">Number of monthly periods covered by the computation.</param>
/// <param name="EffectiveDiscountMonthly">Monthly compounded effective discount factor (8 decimals).</param>
/// <param name="EffectiveAgeYears">Echoed fractional age at the valuation date.</param>
/// <param name="ComputationBreakdownJson">JSON-encoded per-period breakdown (≤ 32 KiB).</param>
public sealed record CapitalisedAnnuityComputationDto(
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal CapitalisedAmountMdl,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int LifeExpectancyMonths,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal EffectiveDiscountMonthly,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal EffectiveAgeYears,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string ComputationBreakdownJson);
