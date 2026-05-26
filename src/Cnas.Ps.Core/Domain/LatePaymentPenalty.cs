namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0819 / TOR BP 1.2-J — late-payment-penalty (majorare de întârziere) row
/// produced when a payer's <see cref="MonthlyContributionCalculation"/> for a
/// reporting month remains unpaid past the statutory due date. One row exists
/// per (<see cref="ContributorId"/>, <see cref="Month"/>, <see cref="UpToDate"/>)
/// tuple — re-running the calculator for the same tuple upserts in place so the
/// natural key is also the idempotency key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Calculation contract.</b> The calculator
/// (<c>ILatePaymentPenaltyCalculator</c>):
/// <list type="number">
///   <item>Loads the <see cref="MonthlyContributionCalculation"/> row for the
///     (contributor, month) tuple — when missing, returns
///     <c>NotFound / MONTHLY_CALC_NOT_FOUND</c>.</item>
///   <item>Treats <see cref="PrincipalAmount"/> as the unpaid contribution
///     principal. <b>Pragmatic shortcut</b>: until a payments ledger exists the
///     calculator assumes 100% unpaid and copies
///     <see cref="MonthlyContributionCalculation.TotalAdjusted"/> verbatim into
///     <see cref="PrincipalAmount"/>. Once R0814 / R0818 land the principal will
///     be reduced by the sum of receipts attributed to the month.</item>
///   <item>Computes <see cref="DueDate"/> as the
///     <c>PenaltyOptions.DueDateOfMonthFollowing</c>-th day of the month after
///     <see cref="Month"/> (e.g. April-2026 → 2026-05-25 by default).</item>
///   <item>Sets <see cref="DaysLate"/> = <c>(UpToDate - DueDate).Days</c>,
///     clamped to zero when <see cref="UpToDate"/> ≤ <see cref="DueDate"/>.</item>
///   <item>Computes
///     <see cref="PenaltyAmount"/> = <c>round(PrincipalAmount × DailyRatePercent / 100 × DaysLate, 2)</c>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Waive workflow.</b> An admin can waive a penalty row via
/// <c>ILatePaymentPenaltyCalculator.WaiveAsync</c>; the original
/// <see cref="PenaltyAmount"/> is preserved verbatim (the CLAUDE.md "Immutable
/// Snapshots" rule) and <see cref="IsWaived"/> is flipped to <c>true</c> with the
/// rationale captured in <see cref="WaiveReason"/>. Waived rows still appear in
/// listings — operators do NOT lose visibility of the audit trail.
/// </para>
/// <para>
/// <b>Natural-key uniqueness.</b> <c>(ContributorId, Month, UpToDate)</c> is
/// unique — re-running the calculation for the same triple updates the row in
/// place. Different <c>UpToDate</c> values yield distinct rows so an operator
/// can chart penalty growth over time.
/// </para>
/// <para>
/// <b>External id.</b> The entity implements <see cref="IExternalId"/> because
/// the outbound DTO (<c>Cnas.Ps.Contracts.LatePaymentPenaltyDto.Id</c>) carries
/// the Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
public sealed class LatePaymentPenalty : AuditableEntity, IExternalId
{
    /// <summary>
    /// Foreign-key reference to the owning <see cref="Contributor"/> whose
    /// unpaid contribution generated this penalty row.
    /// </summary>
    public long ContributorId { get; set; }

    /// <summary>
    /// Calendar month the underlying contribution belongs to (day = 1 by
    /// convention). Matches
    /// <see cref="MonthlyContributionCalculation.Month"/>.
    /// </summary>
    public DateOnly Month { get; set; }

    /// <summary>
    /// Unpaid contribution principal that the penalty is computed against
    /// (MDL). Today the calculator copies
    /// <see cref="MonthlyContributionCalculation.TotalAdjusted"/> verbatim
    /// because no payments-ledger reconciliation exists yet; a future iteration
    /// will subtract the sum of receipts attributed to the month.
    /// </summary>
    public decimal PrincipalAmount { get; set; }

    /// <summary>
    /// UTC instant the calculation was last produced. Updated on every
    /// idempotent re-run so operators can chart freshness.
    /// </summary>
    public DateTime CalculatedAtUtc { get; set; }

    /// <summary>
    /// Statutory due date for the underlying contribution — by default the
    /// <c>PenaltyOptions.DueDateOfMonthFollowing</c>-th day of the month
    /// following <see cref="Month"/>. Persisted so admin reports can chart the
    /// statutory deadline alongside the actual settlement date.
    /// </summary>
    public DateOnly DueDate { get; set; }

    /// <summary>
    /// Cut-off date the penalty is calculated up to. Penalty rows are produced
    /// per (Contributor, Month, UpToDate) so an operator can chart the penalty
    /// growth over time by issuing multiple calculations with progressing
    /// up-to dates.
    /// </summary>
    public DateOnly UpToDate { get; set; }

    /// <summary>
    /// Whole-day count from <see cref="DueDate"/> to <see cref="UpToDate"/>
    /// (inclusive of the up-to date, exclusive of the due date — i.e. one day
    /// late means <c>DaysLate == 1</c>). Zero when
    /// <see cref="UpToDate"/> ≤ <see cref="DueDate"/>.
    /// </summary>
    public int DaysLate { get; set; }

    /// <summary>
    /// Daily penalty rate (percent) effective for the calculation — e.g.
    /// <c>0.03m</c> means 0.03% per day. Sourced from
    /// <c>PenaltyOptions.DailyRatePercent</c> at calculation time; persisted so
    /// re-runs are reproducible even after the regulator updates the rate.
    /// </summary>
    public decimal DailyRatePercent { get; set; }

    /// <summary>
    /// Penalty amount (MDL), rounded half-to-even to two decimal places:
    /// <c>round(PrincipalAmount × DailyRatePercent / 100 × DaysLate, 2)</c>.
    /// Zero when <see cref="DaysLate"/> is zero.
    /// </summary>
    public decimal PenaltyAmount { get; set; }

    /// <summary>
    /// True when an admin has waived the penalty via
    /// <c>ILatePaymentPenaltyCalculator.WaiveAsync</c>. The original
    /// <see cref="PenaltyAmount"/> is preserved — waiving is a flag, not a
    /// destructive update.
    /// </summary>
    public bool IsWaived { get; set; }

    /// <summary>
    /// Operator-supplied rationale for the waive action (3..500 chars when set).
    /// <c>null</c> while the row carries the original penalty unchanged.
    /// </summary>
    public string? WaiveReason { get; set; }
}
