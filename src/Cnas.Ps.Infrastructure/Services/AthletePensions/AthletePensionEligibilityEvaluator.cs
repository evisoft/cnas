using System.Collections.Generic;
using System.Linq;
using Cnas.Ps.Application.AthletePensions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services.AthletePensions;

/// <summary>
/// R1403 / TOR §3.6-D — production implementation of
/// <see cref="IAthletePensionEligibilityEvaluator"/>. Encodes the legal
/// eligibility thresholds for the lifetime athlete-pension as a pure
/// function over the supplied <see cref="AthletePensionEligibilityInputDto"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>PLACEHOLDER thresholds.</b> The numeric thresholds below (athlete
/// retirement age 35, coach minimum 20 years, "≥ 1 Olympic medal", "≥ 1
/// world medal OR record") are PLACEHOLDER values pending the regulatory
/// load. The stable rule codes survive any future revision; only the
/// constants on lines marked <c>PLACEHOLDER</c> below will move when the
/// Government Decision on athlete pensions is loaded.
/// </para>
/// <para>
/// <b>No PII.</b> The evaluator consumes only enum-name codes + dates +
/// numerics. The verdict's <c>Reason</c> and <c>RuleHits</c> embed only
/// stable rule codes — never the beneficiary IDNP, name, or display name.
/// </para>
/// </remarks>
public sealed class AthletePensionEligibilityEvaluator : IAthletePensionEligibilityEvaluator
{
    /// <summary>Stable rule code — athlete retirement-age threshold.</summary>
    public const string RuleAthleteRetirementAge = "R_ATHLETE.RETIREMENT_AGE";

    /// <summary>Stable rule code — athlete Olympic-medal threshold.</summary>
    public const string RuleAthleteOlympicMedal = "R_ATHLETE.OLYMPIC_MEDAL";

    /// <summary>Stable rule code — athlete world-medal-or-record threshold.</summary>
    public const string RuleAthleteWorldMedalOrRecord = "R_ATHLETE.WORLD_MEDAL_OR_RECORD";

    /// <summary>Stable rule code — coach minimum-years-of-service threshold.</summary>
    public const string RuleCoachMinYears = "R_COACH.MIN_YEARS";

    /// <summary>Stable rule code — coach trained-medal-winning-athlete requirement.</summary>
    public const string RuleCoachHasTrainedMedalAthlete = "R_COACH.HAS_TRAINED_ATHLETE_WITH_MEDAL";

    /// <summary>PLACEHOLDER — athlete retirement-age threshold in years.</summary>
    private const int AthleteRetirementAgeYears = 35;

    /// <summary>PLACEHOLDER — coach minimum-years-of-service threshold.</summary>
    private const int CoachMinYears = 20;

    /// <inheritdoc />
    public Result<AthletePensionEligibilityVerdictDto> Evaluate(AthletePensionEligibilityInputDto input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!Enum.TryParse<AthletePensionRole>(input.Role, ignoreCase: false, out var role))
        {
            return Result<AthletePensionEligibilityVerdictDto>.Failure(
                ErrorCodes.ValidationFailed,
                "Role must be a known AthletePensionRole enum name.");
        }

        return role switch
        {
            AthletePensionRole.Athlete => EvaluateAthlete(input),
            AthletePensionRole.Coach => EvaluateCoach(input),
            _ => Result<AthletePensionEligibilityVerdictDto>.Failure(
                ErrorCodes.ValidationFailed,
                "Unsupported role."),
        };
    }

    /// <summary>
    /// Athlete-branch evaluation: requires retirement-age AND (Olympic medal
    /// OR world medal/record).
    /// </summary>
    /// <param name="input">PII-free input envelope.</param>
    /// <returns>Verdict with explain trace.</returns>
    private static Result<AthletePensionEligibilityVerdictDto> EvaluateAthlete(
        AthletePensionEligibilityInputDto input)
    {
        var hits = new List<EligibilityRuleHitDto>();
        var ageYears = WholeYearsBetween(input.BirthDate, input.EvaluationDate);
        var ageOk = ageYears >= AthleteRetirementAgeYears;
        hits.Add(new EligibilityRuleHitDto(
            RuleCode: RuleAthleteRetirementAge,
            AchievementKind: null,
            Year: null,
            Points: ageOk ? 1m : 0m));

        var hasOlympicMedal = input.VerifiedRecords.Any(r =>
            r.AchievementKind is nameof(AthleteAchievementKind.OlympicGold)
                or nameof(AthleteAchievementKind.OlympicSilver)
                or nameof(AthleteAchievementKind.OlympicBronze));
        foreach (var r in input.VerifiedRecords.Where(r =>
            r.AchievementKind is nameof(AthleteAchievementKind.OlympicGold)
                or nameof(AthleteAchievementKind.OlympicSilver)
                or nameof(AthleteAchievementKind.OlympicBronze)))
        {
            hits.Add(new EligibilityRuleHitDto(
                RuleCode: RuleAthleteOlympicMedal,
                AchievementKind: r.AchievementKind,
                Year: r.AchievementYear,
                Points: 1m));
        }

        var hasWorldMedalOrRecord = input.VerifiedRecords.Any(r =>
            r.AchievementKind is nameof(AthleteAchievementKind.WorldChampionGold)
                or nameof(AthleteAchievementKind.WorldChampionSilver)
                or nameof(AthleteAchievementKind.WorldChampionBronze)
                or nameof(AthleteAchievementKind.WorldRecord));
        foreach (var r in input.VerifiedRecords.Where(r =>
            r.AchievementKind is nameof(AthleteAchievementKind.WorldChampionGold)
                or nameof(AthleteAchievementKind.WorldChampionSilver)
                or nameof(AthleteAchievementKind.WorldChampionBronze)
                or nameof(AthleteAchievementKind.WorldRecord)))
        {
            hits.Add(new EligibilityRuleHitDto(
                RuleCode: RuleAthleteWorldMedalOrRecord,
                AchievementKind: r.AchievementKind,
                Year: r.AchievementYear,
                Points: 1m));
        }

        var medalsOk = hasOlympicMedal || hasWorldMedalOrRecord;
        var eligible = ageOk && medalsOk;
        var reason = (ageOk, medalsOk) switch
        {
            (true, true) => "Athlete meets retirement-age + medal thresholds.",
            (false, _) => $"{RuleAthleteRetirementAge} not met (age={ageYears}, threshold={AthleteRetirementAgeYears}).",
            (_, false) => $"{RuleAthleteOlympicMedal} and {RuleAthleteWorldMedalOrRecord} not met.",
        };

        return Result<AthletePensionEligibilityVerdictDto>.Success(new AthletePensionEligibilityVerdictDto(
            IsEligible: eligible,
            Reason: reason,
            RuleHits: hits));
    }

    /// <summary>
    /// Coach-branch evaluation: requires minimum years-of-service AND at
    /// least one medal achievement (representing the trained athletes).
    /// </summary>
    /// <param name="input">PII-free input envelope.</param>
    /// <returns>Verdict with explain trace.</returns>
    private static Result<AthletePensionEligibilityVerdictDto> EvaluateCoach(
        AthletePensionEligibilityInputDto input)
    {
        var hits = new List<EligibilityRuleHitDto>();
        var years = input.VerifiedRecords
            .Where(r => r.AchievementKind == nameof(AthleteAchievementKind.CoachYearsService))
            .Sum(r => r.Years ?? 0);
        var yearsOk = years >= CoachMinYears;
        hits.Add(new EligibilityRuleHitDto(
            RuleCode: RuleCoachMinYears,
            AchievementKind: nameof(AthleteAchievementKind.CoachYearsService),
            Year: null,
            Points: yearsOk ? 1m : 0m));

        var medalKinds = new[]
        {
            nameof(AthleteAchievementKind.OlympicGold),
            nameof(AthleteAchievementKind.OlympicSilver),
            nameof(AthleteAchievementKind.OlympicBronze),
            nameof(AthleteAchievementKind.WorldChampionGold),
            nameof(AthleteAchievementKind.WorldChampionSilver),
            nameof(AthleteAchievementKind.WorldChampionBronze),
            nameof(AthleteAchievementKind.EuropeanChampionGold),
            nameof(AthleteAchievementKind.EuropeanChampionSilver),
            nameof(AthleteAchievementKind.EuropeanChampionBronze),
        };
        var hasMedal = input.VerifiedRecords.Any(r => medalKinds.Contains(r.AchievementKind));
        foreach (var r in input.VerifiedRecords.Where(r => medalKinds.Contains(r.AchievementKind)))
        {
            hits.Add(new EligibilityRuleHitDto(
                RuleCode: RuleCoachHasTrainedMedalAthlete,
                AchievementKind: r.AchievementKind,
                Year: r.AchievementYear,
                Points: 1m));
        }

        var eligible = yearsOk && hasMedal;
        var reason = (yearsOk, hasMedal) switch
        {
            (true, true) => "Coach meets minimum-years-of-service + trained-medal-winning-athlete thresholds.",
            (false, _) => $"{RuleCoachMinYears} not met (years={years}, threshold={CoachMinYears}).",
            (_, false) => $"{RuleCoachHasTrainedMedalAthlete} not met (no medal achievement supplied).",
        };

        return Result<AthletePensionEligibilityVerdictDto>.Success(new AthletePensionEligibilityVerdictDto(
            IsEligible: eligible,
            Reason: reason,
            RuleHits: hits));
    }

    /// <summary>
    /// Returns the count of whole years between <paramref name="birth"/> and
    /// <paramref name="evaluation"/>; clamped at zero when the order is reversed.
    /// </summary>
    /// <param name="birth">Beneficiary date of birth.</param>
    /// <param name="evaluation">Evaluation date.</param>
    /// <returns>Whole years (≥ 0).</returns>
    private static int WholeYearsBetween(DateOnly birth, DateOnly evaluation)
    {
        if (evaluation < birth)
        {
            return 0;
        }
        var years = evaluation.Year - birth.Year;
        if (evaluation.Month < birth.Month
            || (evaluation.Month == birth.Month && evaluation.Day < birth.Day))
        {
            years -= 1;
        }
        return Math.Max(0, years);
    }
}
