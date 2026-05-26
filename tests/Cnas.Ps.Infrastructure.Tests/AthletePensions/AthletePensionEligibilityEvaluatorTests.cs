using Cnas.Ps.Application.AthletePensions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.AthletePensions;

namespace Cnas.Ps.Infrastructure.Tests.AthletePensions;

/// <summary>
/// R1403 / TOR §3.6-D — tests for the eligibility evaluator. Verifies the
/// athlete retirement-age rule, the medal threshold rules, and the coach
/// minimum-years-of-service rule. Inputs carry only enum names + dates —
/// the evaluator is a pure function so no DB / clock plumbing is involved.
/// </summary>
public sealed class AthletePensionEligibilityEvaluatorTests
{
    private static AthletePensionEligibilityInputDto Build(
        AthletePensionRole role,
        DateOnly birthDate,
        DateOnly evalDate,
        IReadOnlyList<EligibilityRecordDto> records) =>
        new(
            Role: role.ToString(),
            BirthDate: birthDate,
            EvaluationDate: evalDate,
            VerifiedRecords: records);

    [Fact]
    public void Athlete_Under35_Ineligible_RetirementAgeFails()
    {
        var sut = new AthletePensionEligibilityEvaluator();
        var input = Build(
            AthletePensionRole.Athlete,
            birthDate: new DateOnly(2000, 1, 1),  // age = 26
            evalDate: new DateOnly(2026, 5, 23),
            records: new[]
            {
                new EligibilityRecordDto(
                    AchievementKind: nameof(AthleteAchievementKind.OlympicGold),
                    AchievementYear: 2020,
                    Years: null),
            });

        var result = sut.Evaluate(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeFalse();
        result.Value.Reason.Should().Contain("RETIREMENT_AGE");
    }

    [Fact]
    public void Athlete_With35Plus_AndOlympicBronze_Eligible()
    {
        var sut = new AthletePensionEligibilityEvaluator();
        var input = Build(
            AthletePensionRole.Athlete,
            birthDate: new DateOnly(1985, 1, 1),  // age = 41
            evalDate: new DateOnly(2026, 5, 23),
            records: new[]
            {
                new EligibilityRecordDto(
                    AchievementKind: nameof(AthleteAchievementKind.OlympicBronze),
                    AchievementYear: 2008,
                    Years: null),
            });

        var result = sut.Evaluate(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
    }

    [Fact]
    public void Athlete_With35Plus_NoMedals_Ineligible()
    {
        var sut = new AthletePensionEligibilityEvaluator();
        var input = Build(
            AthletePensionRole.Athlete,
            birthDate: new DateOnly(1985, 1, 1),
            evalDate: new DateOnly(2026, 5, 23),
            records: Array.Empty<EligibilityRecordDto>());

        var result = sut.Evaluate(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeFalse();
    }

    [Fact]
    public void Coach_20PlusYearsAndMedal_Eligible()
    {
        var sut = new AthletePensionEligibilityEvaluator();
        var input = Build(
            AthletePensionRole.Coach,
            birthDate: new DateOnly(1965, 1, 1),
            evalDate: new DateOnly(2026, 5, 23),
            records: new[]
            {
                new EligibilityRecordDto(
                    AchievementKind: nameof(AthleteAchievementKind.CoachYearsService),
                    AchievementYear: 2024,
                    Years: 25),
                new EligibilityRecordDto(
                    AchievementKind: nameof(AthleteAchievementKind.OlympicSilver),
                    AchievementYear: 2016,
                    Years: null),
            });

        var result = sut.Evaluate(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
    }

    [Fact]
    public void Coach_With19Years_Ineligible()
    {
        var sut = new AthletePensionEligibilityEvaluator();
        var input = Build(
            AthletePensionRole.Coach,
            birthDate: new DateOnly(1965, 1, 1),
            evalDate: new DateOnly(2026, 5, 23),
            records: new[]
            {
                new EligibilityRecordDto(
                    AchievementKind: nameof(AthleteAchievementKind.CoachYearsService),
                    AchievementYear: 2024,
                    Years: 19),
                new EligibilityRecordDto(
                    AchievementKind: nameof(AthleteAchievementKind.OlympicGold),
                    AchievementYear: 2016,
                    Years: null),
            });

        var result = sut.Evaluate(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeFalse();
        result.Value.Reason.Should().Contain("MIN_YEARS");
    }
}
