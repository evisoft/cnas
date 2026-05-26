using System.Collections.Generic;
using System.Text.Json;
using Cnas.Ps.Application.CapitalisedPayments;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services.CapitalisedPayments;

/// <summary>
/// R1202 / TOR §3.4-C — pure-function implementation of
/// <see cref="IPresentValueAnnuityCalculator"/>. Stateless; registered as
/// Singleton.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm.</b> See <see cref="IPresentValueAnnuityCalculator"/> remarks
/// for the high-level description. Internal precision is kept at the
/// <see cref="decimal"/> ceiling (28-29 significant digits); the per-period
/// breakdown stores rounded factors (8 decimals for survival / discount,
/// 2 decimals for contributions) so the JSON payload stays compact.
/// </para>
/// <para>
/// <b>Breakdown sampling.</b> For N ≤ 240 (≤ 20 years) every period is
/// included. Above that threshold the breakdown captures the first 24 +
/// last 24 periods plus an every-12-month sample of the mid-range so the
/// JSON payload stays well below the 32 KiB cap.
/// </para>
/// </remarks>
public sealed class PresentValueAnnuityCalculator : IPresentValueAnnuityCalculator
{
    /// <summary>Stable error code returned when the input fails calculator-level validation.</summary>
    public const string InvalidInputCode = "CAP_PAY.INVALID_CALCULATOR_INPUT";

    /// <summary>Internal threshold above which the breakdown is sampled rather than enumerated period-by-period.</summary>
    public const int FullBreakdownPeriodCap = 240;

    /// <summary>Cached JSON serializer options used by the breakdown writer.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IMortalityTable _mortalityTable;

    /// <summary>Constructs the calculator with its injected mortality-table abstraction.</summary>
    /// <param name="mortalityTable">Mortality-table abstraction consulted for lifetime obligations.</param>
    public PresentValueAnnuityCalculator(IMortalityTable mortalityTable)
    {
        ArgumentNullException.ThrowIfNull(mortalityTable);
        _mortalityTable = mortalityTable;
    }

    /// <inheritdoc />
    public Result<CapitalisedAnnuityComputationDto> Compute(CapitalisedAnnuityInputDto input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!Enum.TryParse<BeneficiarySex>(input.BeneficiarySex, ignoreCase: false, out var sex))
        {
            return Result<CapitalisedAnnuityComputationDto>.Failure(
                InvalidInputCode, "BeneficiarySex must parse to a known BeneficiarySex enum name.");
        }
        if (input.MonthlyAmountMdl <= 0m)
        {
            return Result<CapitalisedAnnuityComputationDto>.Failure(
                InvalidInputCode, "MonthlyAmountMdl must be > 0.");
        }
        if (input.AgeAtValuationYears < 0m || input.AgeAtValuationYears > 110m)
        {
            return Result<CapitalisedAnnuityComputationDto>.Failure(
                InvalidInputCode, "AgeAtValuationYears must lie in [0, 110].");
        }
        if (input.AnnualDiscountRatePercent < 0m || input.AnnualDiscountRatePercent > 30m)
        {
            return Result<CapitalisedAnnuityComputationDto>.Failure(
                InvalidInputCode, "AnnualDiscountRatePercent must lie in [0, 30].");
        }
        if (input.ObligationEndDate.HasValue && input.ObligationEndDate.Value < input.ValuationDate)
        {
            return Result<CapitalisedAnnuityComputationDto>.Failure(
                InvalidInputCode, "ObligationEndDate must be >= ValuationDate when set.");
        }

        // Monthly compounded effective rate from the annual rate.
        // monthlyDiscount = (1 + r/100)^(1/12) - 1
        var monthlyDiscount = ComputeMonthlyEffectiveRate(input.AnnualDiscountRatePercent);

        // Periods N.
        int periods;
        bool isLifetime;
        if (input.ObligationEndDate.HasValue)
        {
            isLifetime = false;
            periods = MonthsBetween(input.ValuationDate, input.ObligationEndDate.Value);
            if (periods < 0)
            {
                periods = 0;
            }
        }
        else
        {
            isLifetime = true;
            var integerAge = (int)Math.Floor(input.AgeAtValuationYears);
            var lookup = _mortalityTable.GetRemainingLifeExpectancyMonths(sex, integerAge);
            if (lookup.IsFailure)
            {
                return Result<CapitalisedAnnuityComputationDto>.Failure(lookup.ErrorCode!, lookup.ErrorMessage!);
            }
            periods = lookup.Value;
        }

        // Accumulate present value. Use full precision internally; the public
        // contract rounds the total to 2 decimals via banker's rounding.
        var monthlyAmount = input.MonthlyAmountMdl;
        var sum = 0m;
        var breakdownRows = new List<BreakdownRow>(capacity: Math.Min(periods, FullBreakdownPeriodCap + 64));
        var sampleIndices = BuildSampleIndices(periods);

        // (1 + monthlyDiscount) reused across periods; we compound iteratively
        // rather than calling Math.Pow to stay in decimal precision.
        var compoundFactor = 1m;
        var onePlusD = 1m + monthlyDiscount;
        for (var t = 1; t <= periods; t++)
        {
            compoundFactor *= onePlusD;
            var discountFactor = 1m / compoundFactor;
            var survivalProb = isLifetime
                ? (decimal)(periods - t + 1) / periods
                : 1m;
            var contribution = monthlyAmount * survivalProb * discountFactor;
            sum += contribution;
            if (sampleIndices.Contains(t))
            {
                breakdownRows.Add(new BreakdownRow(
                    t,
                    decimal.Round(survivalProb, 8, MidpointRounding.ToEven),
                    decimal.Round(discountFactor, 8, MidpointRounding.ToEven),
                    decimal.Round(contribution, 2, MidpointRounding.ToEven)));
            }
        }

        var capitalisedAmount = decimal.Round(sum, 2, MidpointRounding.ToEven);
        var effectiveAgeYears = decimal.Round(input.AgeAtValuationYears, 2, MidpointRounding.ToEven);
        var effectiveDiscountMonthly = decimal.Round(monthlyDiscount, 8, MidpointRounding.ToEven);

        var breakdownEnvelope = new BreakdownEnvelope(
            isLifetime,
            periods,
            sex.ToString(),
            decimal.Round(input.AnnualDiscountRatePercent, 4, MidpointRounding.ToEven),
            effectiveDiscountMonthly,
            "linear-decrement placeholder; replace when full annuity tables ship",
            breakdownRows);
        var breakdownJson = JsonSerializer.Serialize(breakdownEnvelope, CachedJsonOptions);

        return Result<CapitalisedAnnuityComputationDto>.Success(new CapitalisedAnnuityComputationDto(
            CapitalisedAmountMdl: capitalisedAmount,
            LifeExpectancyMonths: periods,
            EffectiveDiscountMonthly: effectiveDiscountMonthly,
            EffectiveAgeYears: effectiveAgeYears,
            ComputationBreakdownJson: breakdownJson));
    }

    /// <summary>
    /// Builds the set of period indices included in the breakdown payload.
    /// For short horizons every index is sampled; for long horizons the
    /// breakdown carries the first 24, the last 24, and every 12th period in
    /// between, so the JSON stays well under 32 KiB.
    /// </summary>
    /// <param name="periods">Total period count.</param>
    /// <returns>Hash-set of 1-based period indices to include in the breakdown.</returns>
    private static HashSet<int> BuildSampleIndices(int periods)
    {
        var set = new HashSet<int>();
        if (periods <= 0)
        {
            return set;
        }
        if (periods <= FullBreakdownPeriodCap)
        {
            for (var t = 1; t <= periods; t++)
            {
                set.Add(t);
            }
            return set;
        }
        for (var t = 1; t <= 24 && t <= periods; t++)
        {
            set.Add(t);
        }
        for (var t = periods; t >= periods - 23 && t >= 1; t--)
        {
            set.Add(t);
        }
        for (var t = 36; t < periods - 23; t += 12)
        {
            set.Add(t);
        }
        return set;
    }

    /// <summary>
    /// Returns <c>(1 + r/100)^(1/12) - 1</c> using Newton-iteration on the
    /// 12th-root to stay in <see cref="decimal"/> precision. For
    /// <c>r = 0</c> the function returns 0 (no discount).
    /// </summary>
    /// <param name="annualPercent">Annual rate (%); <c>[0, 30]</c>.</param>
    /// <returns>Monthly effective rate.</returns>
    internal static decimal ComputeMonthlyEffectiveRate(decimal annualPercent)
    {
        if (annualPercent == 0m)
        {
            return 0m;
        }
        var onePlusR = 1m + (annualPercent / 100m);
        var twelfthRoot = NthRoot(onePlusR, 12);
        return twelfthRoot - 1m;
    }

    /// <summary>
    /// Computes the integer-N-th root of <paramref name="value"/> via
    /// Newton's method in <see cref="decimal"/>. Converges in a handful of
    /// iterations for the small N (12) used by the calculator.
    /// </summary>
    /// <param name="value">Positive radicand (must be ≥ 1 for the calculator's input range).</param>
    /// <param name="n">Positive root order (used as 12 here).</param>
    /// <returns>The N-th root of <paramref name="value"/>.</returns>
    internal static decimal NthRoot(decimal value, int n)
    {
        if (value <= 0m)
        {
            return 0m;
        }
        var nDec = (decimal)n;
        var guess = value < 2m ? 1m : value / nDec; // crude seed
        for (var i = 0; i < 64; i++)
        {
            // x_{k+1} = ( (n - 1) * x_k + value / x_k^{n-1} ) / n
            var pow = decimal.One;
            for (var p = 0; p < n - 1; p++)
            {
                pow *= guess;
            }
            var next = (((nDec - 1m) * guess) + (value / pow)) / nDec;
            if (Math.Abs(next - guess) < 0.0000000000001m)
            {
                return next;
            }
            guess = next;
        }
        return guess;
    }

    /// <summary>
    /// Returns the count of whole months from <paramref name="start"/> to
    /// <paramref name="end"/>; clamped at zero when the order is reversed.
    /// </summary>
    /// <param name="start">Earlier calendar date (valuation date).</param>
    /// <param name="end">Later calendar date (obligation end).</param>
    /// <returns>Difference in whole months (≥ 0).</returns>
    internal static int MonthsBetween(DateOnly start, DateOnly end)
    {
        if (end < start)
        {
            return 0;
        }
        var months = ((end.Year - start.Year) * 12) + (end.Month - start.Month);
        if (end.Day < start.Day)
        {
            months -= 1;
        }
        return Math.Max(0, months);
    }

    /// <summary>One row inside the per-period breakdown payload.</summary>
    /// <param name="T">1-based period index.</param>
    /// <param name="SurvivalProb">Survival probability for the period.</param>
    /// <param name="DiscountFactor">Discount factor for the period.</param>
    /// <param name="Contribution">Per-period contribution to the present-value sum (MDL).</param>
    private sealed record BreakdownRow(int T, decimal SurvivalProb, decimal DiscountFactor, decimal Contribution);

    /// <summary>Top-level breakdown envelope serialised into the decision row.</summary>
    /// <param name="IsLifetime">True when the underlying obligation is lifetime.</param>
    /// <param name="PeriodCount">Total period count covered by the computation.</param>
    /// <param name="Sex">Beneficiary biological sex (enum-name).</param>
    /// <param name="AnnualRatePercent">Annual discount rate echoed into the breakdown.</param>
    /// <param name="EffectiveMonthlyDiscount">Monthly compounded effective rate.</param>
    /// <param name="SurvivalModelNote">Documentation note about the placeholder survival model.</param>
    /// <param name="Rows">Sampled per-period rows.</param>
    private sealed record BreakdownEnvelope(
        bool IsLifetime,
        int PeriodCount,
        string Sex,
        decimal AnnualRatePercent,
        decimal EffectiveMonthlyDiscount,
        string SurvivalModelNote,
        IReadOnlyList<BreakdownRow> Rows);
}
