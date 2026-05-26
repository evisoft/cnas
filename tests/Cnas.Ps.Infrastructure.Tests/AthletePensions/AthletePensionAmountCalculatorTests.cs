using Cnas.Ps.Application.AthletePensions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.AthletePensions;

namespace Cnas.Ps.Infrastructure.Tests.AthletePensions;

/// <summary>
/// R1403 / TOR §3.6-D — tests for the athlete-pension amount calculator.
/// Verifies the base-multiplier table (Olympic / World / European medals plus
/// records), the coach 0.80 factor, and the AdditionalMultipliers composition.
/// </summary>
public sealed class AthletePensionAmountCalculatorTests
{
    [Fact]
    public void OlympicGoldAthlete_Returns250PercentTimesBase()
    {
        var sut = new AthletePensionAmountCalculator();
        var input = new AthletePensionAmountInputDto(
            Role: nameof(AthletePensionRole.Athlete),
            VerifiedRecords: new[]
            {
                new EligibilityRecordDto(
                    AchievementKind: nameof(AthleteAchievementKind.OlympicGold),
                    AchievementYear: 2016,
                    Years: null),
            },
            RegulatoryBaseMdl: 3_000m,
            AdditionalMultipliers: null);

        var result = sut.Compute(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.FinalMultiplierPercent.Should().Be(250m);
        result.Value.MonthlyAmountMdl.Should().Be(7_500m);
    }

    [Fact]
    public void OlympicGoldPlusWorldRecord_AddsTenPercent()
    {
        var sut = new AthletePensionAmountCalculator();
        var input = new AthletePensionAmountInputDto(
            Role: nameof(AthletePensionRole.Athlete),
            VerifiedRecords: new[]
            {
                new EligibilityRecordDto(
                    AchievementKind: nameof(AthleteAchievementKind.OlympicGold),
                    AchievementYear: 2016,
                    Years: null),
                new EligibilityRecordDto(
                    AchievementKind: nameof(AthleteAchievementKind.WorldRecord),
                    AchievementYear: 2018,
                    Years: null),
            },
            RegulatoryBaseMdl: 3_000m,
            AdditionalMultipliers: null);

        var result = sut.Compute(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.FinalMultiplierPercent.Should().Be(260m);
        result.Value.MonthlyAmountMdl.Should().Be(7_800m);
    }

    [Fact]
    public void Coach_WithAthleteOlympicGold_Applies80PercentFactor()
    {
        var sut = new AthletePensionAmountCalculator();
        var input = new AthletePensionAmountInputDto(
            Role: nameof(AthletePensionRole.Coach),
            VerifiedRecords: new[]
            {
                new EligibilityRecordDto(
                    AchievementKind: nameof(AthleteAchievementKind.OlympicGold),
                    AchievementYear: 2016,
                    Years: null),
                new EligibilityRecordDto(
                    AchievementKind: nameof(AthleteAchievementKind.CoachYearsService),
                    AchievementYear: 2024,
                    Years: 25),
            },
            RegulatoryBaseMdl: 3_000m,
            AdditionalMultipliers: null);

        var result = sut.Compute(input);

        result.IsSuccess.Should().BeTrue();
        // 250% × 0.80 = 200%
        result.Value.FinalMultiplierPercent.Should().Be(200m);
        result.Value.MonthlyAmountMdl.Should().Be(6_000m);
    }

    [Fact]
    public void AdditionalMultiplier_AppliedMultiplicatively()
    {
        var sut = new AthletePensionAmountCalculator();
        var input = new AthletePensionAmountInputDto(
            Role: nameof(AthletePensionRole.Athlete),
            VerifiedRecords: new[]
            {
                new EligibilityRecordDto(
                    AchievementKind: nameof(AthleteAchievementKind.OlympicGold),
                    AchievementYear: 2016,
                    Years: null),
            },
            RegulatoryBaseMdl: 3_000m,
            AdditionalMultipliers: new List<decimal> { 1.10m });

        var result = sut.Compute(input);

        result.IsSuccess.Should().BeTrue();
        // 250% × 1.10 = 275%
        result.Value.FinalMultiplierPercent.Should().Be(275m);
        result.Value.MonthlyAmountMdl.Should().Be(8_250m);
    }
}
