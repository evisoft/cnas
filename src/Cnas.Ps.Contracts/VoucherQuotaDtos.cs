using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R1000..R1034 / TOR §3.2-AB..AD — DTOs for the voucher-quota engine that
// gates the spa / rehabilitation / sanatorium passports (3.2-AB / 3.2-AC /
// 3.2-AD). All Id fields are Sqid-encoded per CLAUDE.md RULE 3.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R1000..R1034 — outbound projection of a single
/// <c>cnas.VoucherQuotas</c> row.
/// </summary>
/// <param name="Id">Sqid-encoded quota id.</param>
/// <param name="PassportCode">Stable passport code this row applies to (e.g. <c>3.2-AB</c>).</param>
/// <param name="Year">Calendar year the row applies to.</param>
/// <param name="MonthlyQuota">Operator-configured monthly cap.</param>
/// <param name="AnnualQuota">Operator-configured annual cap.</param>
/// <param name="UsedThisMonth">Reservations counted in the current calendar month.</param>
/// <param name="UsedThisYear">Reservations counted across the calendar year.</param>
/// <param name="UsedMonth">Month-of-year (1..12) the monthly counter refers to.</param>
/// <param name="MonthlyRemaining">Convenience field — <c>MonthlyQuota - UsedThisMonth</c> (or <c>int.MaxValue</c> when uncapped).</param>
/// <param name="AnnualRemaining">Convenience field — <c>AnnualQuota - UsedThisYear</c> (or <c>int.MaxValue</c> when uncapped).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record VoucherQuotaDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string PassportCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Year,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int MonthlyQuota,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int AnnualQuota,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int UsedThisMonth,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int UsedThisYear,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int UsedMonth,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int MonthlyRemaining,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int AnnualRemaining);

/// <summary>
/// R1000..R1034 — quick "is a slot available?" check returned by the
/// availability endpoint without mutating the quota row.
/// </summary>
/// <param name="PassportCode">Stable passport code that was checked.</param>
/// <param name="Year">Calendar year that was checked.</param>
/// <param name="Month">Month-of-year (1..12) that was checked.</param>
/// <param name="MonthlyRemaining">Remaining slots this month (or <c>int.MaxValue</c> when uncapped).</param>
/// <param name="AnnualRemaining">Remaining slots this year (or <c>int.MaxValue</c> when uncapped).</param>
/// <param name="IsAvailable">True iff at least one slot is available against both caps.</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record VoucherQuotaCheckDto(
    string PassportCode,
    int Year,
    int Month,
    int MonthlyRemaining,
    int AnnualRemaining,
    bool IsAvailable);

/// <summary>
/// R1000..R1034 — input envelope for configuring (or re-configuring) a
/// voucher quota for a specific passport-year tuple.
/// </summary>
/// <param name="MonthlyQuota">Monthly cap (≥ 0; 0 means uncapped).</param>
/// <param name="AnnualQuota">Annual cap (≥ 0; 0 means uncapped).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record VoucherQuotaConfigureInputDto(
    int MonthlyQuota,
    int AnnualQuota);
