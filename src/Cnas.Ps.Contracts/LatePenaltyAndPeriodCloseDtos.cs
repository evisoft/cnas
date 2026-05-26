using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0819 / R0820 — Late penalty + management-period close (BP 1.2-J / BP 1.2-K)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0819 / BP 1.2-J — one late-payment-penalty row as it leaves the system.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying penalty row.</param>
/// <param name="ContributorSqid">Sqid-encoded id of the owning payer.</param>
/// <param name="Month">Calendar month the underlying contribution belongs to (day = 1).</param>
/// <param name="PrincipalAmount">Unpaid contribution principal the penalty is computed against (MDL).</param>
/// <param name="CalculatedAtUtc">UTC instant the penalty was last calculated.</param>
/// <param name="DueDate">Statutory due date the calculation anchors against.</param>
/// <param name="UpToDate">Cut-off date the penalty was calculated up to.</param>
/// <param name="DaysLate">Whole-day count from <paramref name="DueDate"/> to <paramref name="UpToDate"/>.</param>
/// <param name="DailyRatePercent">Daily penalty rate (percent) effective for the calculation.</param>
/// <param name="PenaltyAmount">Calculated penalty (MDL), rounded to two decimals.</param>
/// <param name="IsWaived">True when an admin has waived the penalty.</param>
/// <param name="WaiveReason">Waive rationale, when set.</param>
public sealed record LatePaymentPenaltyDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ContributorSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly Month,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal PrincipalAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime CalculatedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly DueDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly UpToDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int DaysLate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    decimal DailyRatePercent,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal PenaltyAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool IsWaived,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? WaiveReason);

/// <summary>
/// R0819 / BP 1.2-J — input DTO for the
/// <c>POST /api/contributors/{contributorSqid}/late-penalty/calculate</c>
/// endpoint. Carries the reporting <see cref="Month"/> and the cut-off
/// <see cref="UpToDate"/> the penalty is calculated up to.
/// </summary>
/// <param name="Month">Calendar month the underlying contribution belongs to (day = 1).</param>
/// <param name="UpToDate">Cut-off date the penalty is calculated up to (must be ≥ Month).</param>
public sealed record LatePaymentPenaltyCalculateInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly Month,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly UpToDate);

/// <summary>
/// R0819 / BP 1.2-J — input DTO for the
/// <c>POST /api/late-penalties/{sqid}/waive</c> admin endpoint.
/// </summary>
/// <param name="Reason">Operator-supplied rationale for the waive action (3..500 chars).</param>
public sealed record LatePaymentPenaltyWaiveInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);

/// <summary>
/// R0820 / BP 1.2-K — one management-period closure row as it leaves the system.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying close row.</param>
/// <param name="Month">Calendar month the closure covers (day = 1).</param>
/// <param name="ClosedAtUtc">UTC instant the closure was recorded.</param>
/// <param name="ClosedByUserSqid">Sqid-encoded id of the closing operator.</param>
/// <param name="Notes">Operator note attached to the close, when set.</param>
/// <param name="TotalDeclaredAcrossPayers">Sum of adjusted contributions across every payer at close time (MDL).</param>
/// <param name="TotalPaidAcrossPayers">Sum of paid contributions across every payer at close time (MDL).</param>
/// <param name="PayerCount">Distinct payers covered by the month's rolls.</param>
/// <param name="DeclarationCount">Total non-cancelled declarations across the month.</param>
/// <param name="IsReopened">True when an admin has re-opened the closed month.</param>
/// <param name="ReopenedAtUtc">UTC instant the re-open was recorded, when set.</param>
/// <param name="ReopenedByUserSqid">Sqid-encoded id of the re-opening operator, when set.</param>
/// <param name="ReopenReason">Re-open rationale, when set.</param>
public sealed record ManagementPeriodCloseDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly Month,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime ClosedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ClosedByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal TotalDeclaredAcrossPayers,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal TotalPaidAcrossPayers,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int PayerCount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int DeclarationCount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool IsReopened,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? ReopenedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ReopenedByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ReopenReason);

/// <summary>
/// R0820 / BP 1.2-K — input DTO for the
/// <c>POST /api/management-period/{month}/close</c> endpoint.
/// </summary>
/// <param name="Month">Calendar month to close (day = 1).</param>
/// <param name="Notes">Optional operator note (≤ 1000 chars when supplied).</param>
public sealed record ManagementPeriodCloseInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly Month,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes = null);

/// <summary>
/// R0820 / BP 1.2-K — input DTO for the
/// <c>POST /api/management-period/{month}/reopen</c> admin endpoint.
/// </summary>
/// <param name="Month">Calendar month to re-open (day = 1).</param>
/// <param name="Reason">Operator-supplied rationale (3..500 chars).</param>
public sealed record ManagementPeriodReopenInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly Month,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);
