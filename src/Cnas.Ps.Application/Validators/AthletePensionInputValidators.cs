using System.Linq;
using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R1403 / TOR §3.6-D — shared constants and helpers for the athlete-pension
/// validators. Centralised so the magic numbers do not drift across rule
/// sets.
/// </summary>
internal static class AthletePensionValidatorShared
{
    /// <summary>Minimum permitted reason / note / verification-note length.</summary>
    public const int ReasonMinLength = 3;

    /// <summary>Maximum permitted reason / note / verification-note length.</summary>
    public const int ReasonMaxLength = 1000;

    /// <summary>Maximum permitted display-name length.</summary>
    public const int NameMaxLength = 256;

    /// <summary>Minimum permitted display-name length.</summary>
    public const int NameMinLength = 3;

    /// <summary>Maximum permitted regulatory base amount (MDL).</summary>
    public const decimal MaxAmount = 100_000_000m;

    /// <summary>Minimum beneficiary age (years) at evaluation date.</summary>
    public const int MinAgeYears = 16;

    /// <summary>Maximum beneficiary age (years) at evaluation date.</summary>
    public const int MaxAgeYears = 110;

    /// <summary>Lower bound on the achievement year (calendar year).</summary>
    public const int MinAchievementYear = 1900;

    /// <summary>Minimum coach years-of-service value.</summary>
    public const int MinCoachYears = 1;

    /// <summary>Maximum coach years-of-service value.</summary>
    public const int MaxCoachYears = 80;

    /// <summary>Maximum event-name length.</summary>
    public const int EventMaxLength = 256;

    /// <summary>Minimum event-name length.</summary>
    public const int EventMinLength = 3;

    /// <summary>Lower bound on each additional multiplier (inclusive).</summary>
    public const decimal MinAdditionalMultiplier = 0.5m;

    /// <summary>Upper bound on each additional multiplier (inclusive).</summary>
    public const decimal MaxAdditionalMultiplier = 3.0m;

    /// <summary>Maximum evidence-document-reference length.</summary>
    public const int EvidenceRefMaxLength = 256;

    /// <summary>Maximum sport-discipline length.</summary>
    public const int SportDisciplineMaxLength = 128;

    /// <summary>Compiled IDNP regex — exactly 13 ASCII digits.</summary>
    public static readonly Regex ThirteenDigitsRegex = new(
        "^[0-9]{13}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    /// <summary>Compiled sport-discipline regex — uppercase + underscores + digits.</summary>
    public static readonly Regex SportDisciplineRegex = new(
        "^[A-Z][A-Z0-9_]{1,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    /// <summary>True when <paramref name="role"/> parses to a known <see cref="AthletePensionRole"/> name (case-sensitive).</summary>
    /// <param name="role">Candidate enum-name string.</param>
    /// <returns>True when the value parses.</returns>
    public static bool IsValidRole(string? role) =>
        role is not null && Enum.TryParse<AthletePensionRole>(role, ignoreCase: false, out _);

    /// <summary>True when <paramref name="sex"/> parses to a known <see cref="BeneficiarySex"/> name (case-sensitive).</summary>
    /// <param name="sex">Candidate enum-name string.</param>
    /// <returns>True when the value parses.</returns>
    public static bool IsValidSex(string? sex) =>
        sex is not null && Enum.TryParse<BeneficiarySex>(sex, ignoreCase: false, out _);

    /// <summary>True when <paramref name="status"/> parses to a known <see cref="AthletePensionAwardStatus"/> name.</summary>
    /// <param name="status">Candidate enum-name string.</param>
    /// <returns>True when the value parses.</returns>
    public static bool IsValidStatus(string? status) =>
        status is not null && Enum.TryParse<AthletePensionAwardStatus>(status, ignoreCase: false, out _);

    /// <summary>True when <paramref name="kind"/> parses to a known <see cref="AthleteAchievementKind"/> name.</summary>
    /// <param name="kind">Candidate enum-name string.</param>
    /// <returns>True when the value parses.</returns>
    public static bool IsValidAchievementKind(string? kind) =>
        kind is not null && Enum.TryParse<AthleteAchievementKind>(kind, ignoreCase: false, out _);

    /// <summary>True when <paramref name="discipline"/> matches the sport-discipline regex.</summary>
    /// <param name="discipline">Candidate sport-discipline code.</param>
    /// <returns>True when the value matches.</returns>
    public static bool IsValidSportDiscipline(string? discipline) =>
        discipline is not null && SportDisciplineRegex.IsMatch(discipline);

    /// <summary>
    /// Returns the count of whole years between <paramref name="birth"/> and
    /// <paramref name="evaluation"/>; clamped at zero when the order is reversed.
    /// </summary>
    /// <param name="birth">Beneficiary date of birth.</param>
    /// <param name="evaluation">Evaluation date.</param>
    /// <returns>Whole years between the two dates (≥ 0).</returns>
    public static int AgeYears(DateOnly birth, DateOnly evaluation)
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

/// <summary>
/// R1403 — validates <see cref="AthletePensionAwardCreateInputDto"/>.
/// Enforces IDNP format, display-name length, birth-date in-past + age range,
/// role / sex enum-name parse, sport-discipline regex.
/// </summary>
public sealed class AthletePensionAwardCreateInputValidator
    : AbstractValidator<AthletePensionAwardCreateInputDto>
{
    /// <summary>Wires the rule set against an injected <see cref="ICnasTimeProvider"/>.</summary>
    /// <param name="clock">UTC clock used to compute the date-based cut-offs.</param>
    public AthletePensionAwardCreateInputValidator(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var todayUtc = DateOnly.FromDateTime(clock.UtcNow);

        RuleFor(x => x.BeneficiaryIdnp)
            .NotEmpty().WithMessage("BeneficiaryIdnp is required.")
            .Must(idnp => AthletePensionValidatorShared.ThirteenDigitsRegex.IsMatch(idnp ?? string.Empty))
            .WithMessage("BeneficiaryIdnp must be exactly 13 digits.");

        RuleFor(x => x.BeneficiaryDisplayName)
            .NotEmpty().WithMessage("BeneficiaryDisplayName is required.")
            .MinimumLength(AthletePensionValidatorShared.NameMinLength)
            .WithMessage($"BeneficiaryDisplayName must be at least {AthletePensionValidatorShared.NameMinLength} characters.")
            .MaximumLength(AthletePensionValidatorShared.NameMaxLength)
            .WithMessage($"BeneficiaryDisplayName cannot exceed {AthletePensionValidatorShared.NameMaxLength} characters.");

        RuleFor(x => x.BeneficiaryBirthDate)
            .Must(d => d <= todayUtc)
            .WithMessage("BeneficiaryBirthDate cannot be in the future.");

        RuleFor(x => x)
            .Must(x =>
            {
                var age = AthletePensionValidatorShared.AgeYears(x.BeneficiaryBirthDate, todayUtc);
                return age >= AthletePensionValidatorShared.MinAgeYears
                    && age <= AthletePensionValidatorShared.MaxAgeYears;
            })
            .WithMessage(
                $"Computed age at today's UTC date must be in [{AthletePensionValidatorShared.MinAgeYears}, " +
                $"{AthletePensionValidatorShared.MaxAgeYears}] years.");

        RuleFor(x => x.BeneficiarySex)
            .NotEmpty().WithMessage("BeneficiarySex is required.")
            .Must(AthletePensionValidatorShared.IsValidSex)
            .WithMessage("BeneficiarySex must be one of Male, Female.");

        RuleFor(x => x.Role)
            .NotEmpty().WithMessage("Role is required.")
            .Must(AthletePensionValidatorShared.IsValidRole)
            .WithMessage("Role must be one of Athlete, Coach.");

        RuleFor(x => x.SportDiscipline)
            .NotEmpty().WithMessage("SportDiscipline is required.")
            .Must(AthletePensionValidatorShared.IsValidSportDiscipline)
            .WithMessage("SportDiscipline must match the pattern ^[A-Z][A-Z0-9_]{1,127}$.");
    }
}

/// <summary>
/// R1403 — validates <see cref="AthleteCareerRecordInputDto"/>. Enforces
/// achievement-kind / year bounds, event-name length, and the
/// CoachYearsService-only Years requirement.
/// </summary>
public sealed class AthleteCareerRecordInputValidator
    : AbstractValidator<AthleteCareerRecordInputDto>
{
    /// <summary>Wires the rule set.</summary>
    /// <param name="clock">UTC clock used to compute the year upper bound.</param>
    public AthleteCareerRecordInputValidator(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var currentYear = clock.UtcNow.Year;

        RuleFor(x => x.AchievementKind)
            .NotEmpty().WithMessage("AchievementKind is required.")
            .Must(AthletePensionValidatorShared.IsValidAchievementKind)
            .WithMessage("AchievementKind must be a known AthleteAchievementKind enum name.");

        RuleFor(x => x.AchievementYear)
            .InclusiveBetween(AthletePensionValidatorShared.MinAchievementYear, currentYear)
            .WithMessage($"AchievementYear must be between {AthletePensionValidatorShared.MinAchievementYear} and {currentYear}.");

        RuleFor(x => x.Event)
            .NotEmpty().WithMessage("Event is required.")
            .MinimumLength(AthletePensionValidatorShared.EventMinLength)
            .WithMessage($"Event must be at least {AthletePensionValidatorShared.EventMinLength} characters.")
            .MaximumLength(AthletePensionValidatorShared.EventMaxLength)
            .WithMessage($"Event cannot exceed {AthletePensionValidatorShared.EventMaxLength} characters.");

        RuleFor(x => x)
            .Must(x => x.AchievementKind != nameof(AthleteAchievementKind.CoachYearsService) || x.Years.HasValue)
            .WithMessage("Years is required when AchievementKind is CoachYearsService.");

        RuleFor(x => x.Years!.Value)
            .InclusiveBetween(AthletePensionValidatorShared.MinCoachYears, AthletePensionValidatorShared.MaxCoachYears)
            .WithMessage($"Years must be between {AthletePensionValidatorShared.MinCoachYears} and {AthletePensionValidatorShared.MaxCoachYears} for CoachYearsService records.")
            .When(x => x.AchievementKind == nameof(AthleteAchievementKind.CoachYearsService) && x.Years.HasValue);

        RuleFor(x => x.EvidenceDocumentReference!)
            .MaximumLength(AthletePensionValidatorShared.EvidenceRefMaxLength)
            .WithMessage($"EvidenceDocumentReference cannot exceed {AthletePensionValidatorShared.EvidenceRefMaxLength} characters.")
            .When(x => !string.IsNullOrEmpty(x.EvidenceDocumentReference));
    }
}

/// <summary>
/// R1403 — validates <see cref="AthleteCareerRecordVerificationInputDto"/>.
/// Enforces the 3..1000 char verification-note shape.
/// </summary>
public sealed class AthleteCareerRecordVerificationInputValidator
    : AbstractValidator<AthleteCareerRecordVerificationInputDto>
{
    /// <summary>Wires the rule set.</summary>
    public AthleteCareerRecordVerificationInputValidator()
    {
        RuleFor(x => x.VerificationNote)
            .NotEmpty().WithMessage("VerificationNote is required.")
            .MinimumLength(AthletePensionValidatorShared.ReasonMinLength)
            .WithMessage($"VerificationNote must be at least {AthletePensionValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(AthletePensionValidatorShared.ReasonMaxLength)
            .WithMessage($"VerificationNote cannot exceed {AthletePensionValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R1403 — validates <see cref="AthletePensionApprovalInputDto"/>. Enforces
/// note length, regulatory-base bounds, and the additional-multipliers
/// bounds.
/// </summary>
public sealed class AthletePensionApprovalInputValidator
    : AbstractValidator<AthletePensionApprovalInputDto>
{
    /// <summary>Wires the rule set.</summary>
    public AthletePensionApprovalInputValidator()
    {
        RuleFor(x => x.Note)
            .NotEmpty().WithMessage("Note is required.")
            .MinimumLength(AthletePensionValidatorShared.ReasonMinLength)
            .WithMessage($"Note must be at least {AthletePensionValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(AthletePensionValidatorShared.ReasonMaxLength)
            .WithMessage($"Note cannot exceed {AthletePensionValidatorShared.ReasonMaxLength} characters.");

        RuleFor(x => x.RegulatoryBaseMdl)
            .GreaterThan(0m).WithMessage("RegulatoryBaseMdl must be > 0.")
            .LessThanOrEqualTo(AthletePensionValidatorShared.MaxAmount)
            .WithMessage($"RegulatoryBaseMdl cannot exceed {AthletePensionValidatorShared.MaxAmount:0}.");

        RuleFor(x => x.AdditionalMultipliers!)
            .Must(list => list.All(m =>
                m >= AthletePensionValidatorShared.MinAdditionalMultiplier
                && m <= AthletePensionValidatorShared.MaxAdditionalMultiplier))
            .WithMessage(
                $"Each additional multiplier must be in [{AthletePensionValidatorShared.MinAdditionalMultiplier}, " +
                $"{AthletePensionValidatorShared.MaxAdditionalMultiplier}].")
            .When(x => x.AdditionalMultipliers is not null && x.AdditionalMultipliers.Count > 0);
    }
}

/// <summary>
/// R1403 — validates <see cref="AthletePensionActivationInputDto"/>.
/// Enforces effective-from ≥ today and note length.
/// </summary>
public sealed class AthletePensionActivationInputValidator
    : AbstractValidator<AthletePensionActivationInputDto>
{
    /// <summary>Wires the rule set.</summary>
    /// <param name="clock">UTC clock used to compute today's date.</param>
    public AthletePensionActivationInputValidator(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var todayUtc = DateOnly.FromDateTime(clock.UtcNow);

        RuleFor(x => x.EffectiveFrom)
            .Must(d => d >= todayUtc)
            .WithMessage("EffectiveFrom must be greater than or equal to today's UTC date.");

        RuleFor(x => x.Note)
            .NotEmpty().WithMessage("Note is required.")
            .MinimumLength(AthletePensionValidatorShared.ReasonMinLength)
            .WithMessage($"Note must be at least {AthletePensionValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(AthletePensionValidatorShared.ReasonMaxLength)
            .WithMessage($"Note cannot exceed {AthletePensionValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R1403 — validates <see cref="AthletePensionReasonInputDto"/> used by
/// reject / suspend / resume / terminate endpoints. Enforces the 3..1000
/// char reason shape.
/// </summary>
public sealed class AthletePensionReasonInputValidator
    : AbstractValidator<AthletePensionReasonInputDto>
{
    /// <summary>Wires the rule set.</summary>
    public AthletePensionReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(AthletePensionValidatorShared.ReasonMinLength)
            .WithMessage($"Reason must be at least {AthletePensionValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(AthletePensionValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason cannot exceed {AthletePensionValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R1403 — validates <see cref="AthletePensionAwardFilterDto"/>. Enforces
/// the Skip / Take page-window bounds and rejects unknown enum names.
/// </summary>
public sealed class AthletePensionAwardFilterValidator
    : AbstractValidator<AthletePensionAwardFilterDto>
{
    /// <summary>Maximum permitted page size.</summary>
    public const int MaxTake = 100;

    /// <summary>Wires the rule set.</summary>
    public AthletePensionAwardFilterValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0).WithMessage("Skip must be >= 0.");

        RuleFor(x => x.Take)
            .InclusiveBetween(1, MaxTake)
            .WithMessage($"Take must be between 1 and {MaxTake}.");

        RuleFor(x => x.Status!)
            .Must(AthletePensionValidatorShared.IsValidStatus)
            .WithMessage("Status must be a known AthletePensionAwardStatus enum name.")
            .When(x => !string.IsNullOrEmpty(x.Status));

        RuleFor(x => x.Role!)
            .Must(AthletePensionValidatorShared.IsValidRole)
            .WithMessage("Role must be a known AthletePensionRole enum name.")
            .When(x => !string.IsNullOrEmpty(x.Role));

        RuleFor(x => x.SportDiscipline!)
            .Must(AthletePensionValidatorShared.IsValidSportDiscipline)
            .WithMessage("SportDiscipline must match the pattern ^[A-Z][A-Z0-9_]{1,127}$.")
            .When(x => !string.IsNullOrEmpty(x.SportDiscipline));
    }
}
