namespace Cnas.Ps.Infrastructure.Services.Penalties;

/// <summary>
/// R0819 / TOR BP 1.2-J — configuration anchor for the late-payment-penalty
/// calculator. Bound from <c>Cnas:Penalty</c>; defaults are baked in so the
/// service works without a per-environment configuration override during
/// early development.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stable defaults.</b> The daily-rate default of 0.03% per day matches
/// the figure published by CNAS for the current contributory regime. It is
/// intentionally configurable because the regulator updates it from time to
/// time; changing the value does NOT require a code release.
/// </para>
/// <para>
/// <b>Due-date convention.</b> The TOR specifies the 25th day of the month
/// following the reporting month as the statutory due date for SI BASS
/// contributions. The configurable
/// <see cref="DueDateOfMonthFollowing"/> lets us track future legislative
/// changes without recompilation.
/// </para>
/// </remarks>
public sealed class PenaltyOptions
{
    /// <summary>Bound from the <c>Cnas:Penalty</c> section.</summary>
    public const string SectionName = "Cnas:Penalty";

    /// <summary>
    /// Daily penalty rate (percent — e.g. <c>0.03m</c> means 0.03% per day).
    /// Applied to the unpaid principal for every day past the
    /// <see cref="DueDateOfMonthFollowing"/>-th day of the month following
    /// the reporting month.
    /// </summary>
    public decimal DailyRatePercent { get; set; } = 0.03m;

    /// <summary>
    /// Day-of-month (1..28) in the month FOLLOWING the reporting month at
    /// which the contribution becomes overdue. The TOR-published convention
    /// is the 25th day of the following month.
    /// </summary>
    public int DueDateOfMonthFollowing { get; set; } = 25;
}
