using Cnas.Ps.Application.CapitalisedPayments;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services.CapitalisedPayments;

/// <summary>
/// R1202 / TOR §3.4-C — <b>PLACEHOLDER</b> mortality table loosely modelled on
/// United Nations 2019 life-expectancy data for Moldova. The values here are
/// NOT the official regulatory series; the table will be replaced by the
/// official Moldova mortality table (BNS / Biroul Național de Statistică)
/// once the regulatory annex is loaded. The interface
/// (<see cref="IMortalityTable"/>) is stable; only the numeric coefficients
/// change when the official table arrives.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a placeholder.</b> The capitalised-payment computation engine ships
/// independently from the regulatory data; deferring the engine until the
/// official series is loaded would block downstream work (REST surface, audit,
/// metrics, tests). The placeholder values are deliberately conservative for
/// male age 0 (≈68.5 yr) / age 30 (≈47 yr) / age 60 (≈18.5 yr) / age 90
/// (≈3 yr) and female age 0 (≈77 yr) / age 30 (≈52 yr) / age 60 (≈22 yr) /
/// age 90 (≈4 yr) and interpolate linearly between anchor ages.
/// </para>
/// <para>
/// <b>Singleton.</b> The implementation is stateless and thread-safe — the DI
/// registration is <c>AddSingleton</c>.
/// </para>
/// </remarks>
public sealed class MoldovaPlaceholderMortalityTable : IMortalityTable
{
    /// <summary>Stable error code emitted when the queried age is outside <c>[0, 110]</c>.</summary>
    public const string AgeOutOfRangeCode = "MORTALITY.AGE_OUT_OF_RANGE";

    /// <summary>Inclusive lower bound on integer age in years.</summary>
    public const int MinAgeYears = 0;

    /// <summary>Inclusive upper bound on integer age in years.</summary>
    public const int MaxAgeYears = 110;

    /// <summary>
    /// Anchor ages (years) at which the placeholder series carries an explicit
    /// month-count. Values for in-between ages are linearly interpolated; ages
    /// strictly above the highest anchor and below the upper bound are
    /// clamped to a residual 12-month floor so the calculator never receives 0.
    /// </summary>
    private static readonly int[] AnchorAges = [0, 30, 60, 90];

    /// <summary>
    /// Male life-expectancy series (months remaining at each anchor age),
    /// loosely modelled on UN 2019 data for Moldova. <b>PLACEHOLDER</b> —
    /// replace with the official BNS series.
    /// </summary>
    private static readonly int[] MaleAnchorMonths = [822, 564, 222, 36];

    /// <summary>
    /// Female life-expectancy series (months remaining at each anchor age),
    /// loosely modelled on UN 2019 data for Moldova. <b>PLACEHOLDER</b> —
    /// replace with the official BNS series.
    /// </summary>
    private static readonly int[] FemaleAnchorMonths = [924, 624, 264, 48];

    /// <summary>Residual life-expectancy floor (months) applied to extreme ages above the top anchor.</summary>
    private const int ResidualFloorMonths = 12;

    /// <inheritdoc />
    public Result<int> GetRemainingLifeExpectancyMonths(BeneficiarySex sex, int integerAgeYears)
    {
        if (integerAgeYears < MinAgeYears || integerAgeYears > MaxAgeYears)
        {
            return Result<int>.Failure(
                AgeOutOfRangeCode,
                $"Age {integerAgeYears} is outside the supported range [{MinAgeYears}, {MaxAgeYears}].");
        }

        var anchors = sex == BeneficiarySex.Female ? FemaleAnchorMonths : MaleAnchorMonths;

        // Above the highest anchor — clamp to the residual floor so the
        // calculator always has at least one period to amortise.
        if (integerAgeYears >= AnchorAges[^1])
        {
            return Result<int>.Success(ResidualFloorMonths);
        }

        // Find the bracketing anchors and linearly interpolate.
        for (var i = 0; i < AnchorAges.Length - 1; i++)
        {
            var lowAge = AnchorAges[i];
            var highAge = AnchorAges[i + 1];
            if (integerAgeYears < lowAge)
            {
                // Below the first anchor — return the first-anchor value
                // (the validator forbids negative ages so this branch only
                // fires when an explicit override is supplied for age 0).
                return Result<int>.Success(anchors[i]);
            }
            if (integerAgeYears >= lowAge && integerAgeYears <= highAge)
            {
                var fraction = (decimal)(integerAgeYears - lowAge) / (highAge - lowAge);
                var lowMonths = anchors[i];
                var highMonths = anchors[i + 1];
                var interpolated = lowMonths + (int)Math.Round((highMonths - lowMonths) * fraction, MidpointRounding.AwayFromZero);
                return Result<int>.Success(Math.Max(ResidualFloorMonths, interpolated));
            }
        }

        // Defensive fallback — should never reach here given the bounds check above.
        return Result<int>.Success(ResidualFloorMonths);
    }
}
