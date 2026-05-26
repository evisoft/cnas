using System.Text.Json;
using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R1201 / R1402 / TOR §3.4-B / §3.6-C — shared constants and helpers for
/// the international-agreements routing validators. Centralised so the
/// magic numbers do not drift across rule sets.
/// </summary>
internal static class IntlAgreementValidatorShared
{
    /// <summary>Minimum permitted note / reason length.</summary>
    public const int NoteMinLength = 3;

    /// <summary>Maximum permitted note / reason length.</summary>
    public const int NoteMaxLength = 2000;

    /// <summary>Maximum permitted display-name length.</summary>
    public const int NameMaxLength = 256;

    /// <summary>Minimum permitted display-name length.</summary>
    public const int NameMinLength = 3;

    /// <summary>Maximum evidence-JSON payload length.</summary>
    public const int EvidenceJsonMaxLength = 16_384;

    /// <summary>Maximum reference-benefit-passport Sqid length.</summary>
    public const int ReferenceSqidMaxLength = 32;

    /// <summary>Maximum filter page-size.</summary>
    public const int MaxTake = 100;

    /// <summary>Compiled IDNP regex — exactly 13 ASCII digits.</summary>
    public static readonly Regex ThirteenDigitsRegex = new(
        "^[0-9]{13}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// Compiled bilateral-agreement-code regex. Format:
    /// <c>{HOST_COUNTRY_ISO2}_MD_{YEAR}</c>, e.g. <c>RO_MD_2006</c>.
    /// </summary>
    public static readonly Regex AgreementCodeRegex = new(
        "^[A-Z]{2}_MD_[0-9]{4}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    /// <summary>Compiled ISO-3166 alpha-2 host-country regex.</summary>
    public static readonly Regex HostCountryRegex = new(
        "^[A-Z]{2}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    /// <summary>True when <paramref name="value"/> parses to a known <see cref="IntlAgreementBenefitKind"/>.</summary>
    /// <param name="value">Candidate enum-name string.</param>
    /// <returns>True when the value parses (case-sensitive).</returns>
    public static bool IsValidBenefitKind(string? value) =>
        value is not null
        && Enum.TryParse<IntlAgreementBenefitKind>(value, ignoreCase: false, out _);

    /// <summary>True when <paramref name="value"/> parses to a known <see cref="IntlAgreementReviewCaseStatus"/>.</summary>
    /// <param name="value">Candidate enum-name string.</param>
    /// <returns>True when the value parses (case-sensitive).</returns>
    public static bool IsValidStatus(string? value) =>
        value is not null
        && Enum.TryParse<IntlAgreementReviewCaseStatus>(value, ignoreCase: false, out _);

    /// <summary>True when <paramref name="value"/> parses to a known <see cref="IntlAgreementReviewLevel"/>.</summary>
    /// <param name="value">Candidate enum-name string.</param>
    /// <returns>True when the value parses (case-sensitive).</returns>
    public static bool IsValidLevel(string? value) =>
        value is not null
        && Enum.TryParse<IntlAgreementReviewLevel>(value, ignoreCase: false, out _);

    /// <summary>True when <paramref name="value"/> parses to a known <see cref="IntlAgreementReviewStepOutcome"/>.</summary>
    /// <param name="value">Candidate enum-name string.</param>
    /// <returns>True when the value parses (case-sensitive).</returns>
    public static bool IsValidStepOutcome(string? value) =>
        value is not null
        && Enum.TryParse<IntlAgreementReviewStepOutcome>(value, ignoreCase: false, out _);

    /// <summary>
    /// True when <paramref name="json"/> is either <c>null</c>, empty, or
    /// successfully parses as JSON. We do not enforce a specific shape here
    /// — the per-benefit-kind <c>IIntlAgreementRoutingPolicy</c> owns its
    /// schema check.
    /// </summary>
    /// <param name="json">Candidate JSON string.</param>
    /// <returns>True when the value parses (or is absent).</returns>
    public static bool IsValidJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// R1201 / R1402 — validates
/// <see cref="IntlAgreementReviewCaseCreateInputDto"/>. Enforces enum
/// membership, IDNP format, agreement-code regex, host-country shape,
/// display-name length, and evidence-JSON validity + size.
/// </summary>
public sealed class IntlAgreementReviewCaseCreateInputValidator
    : AbstractValidator<IntlAgreementReviewCaseCreateInputDto>
{
    /// <summary>Wires the rule set.</summary>
    public IntlAgreementReviewCaseCreateInputValidator()
    {
        RuleFor(x => x.BenefitKind)
            .NotEmpty().WithMessage("BenefitKind is required.")
            .Must(IntlAgreementValidatorShared.IsValidBenefitKind)
            .WithMessage("BenefitKind must be one of IncapacityMaternity, Unemployment.");

        RuleFor(x => x.BeneficiaryIdnp)
            .NotEmpty().WithMessage("BeneficiaryIdnp is required.")
            .Must(idnp => IntlAgreementValidatorShared.ThirteenDigitsRegex.IsMatch(idnp ?? string.Empty))
            .WithMessage("BeneficiaryIdnp must be exactly 13 digits.");

        RuleFor(x => x.BeneficiaryDisplayName)
            .NotEmpty().WithMessage("BeneficiaryDisplayName is required.")
            .MinimumLength(IntlAgreementValidatorShared.NameMinLength)
            .WithMessage($"BeneficiaryDisplayName must be at least {IntlAgreementValidatorShared.NameMinLength} characters.")
            .MaximumLength(IntlAgreementValidatorShared.NameMaxLength)
            .WithMessage($"BeneficiaryDisplayName cannot exceed {IntlAgreementValidatorShared.NameMaxLength} characters.");

        RuleFor(x => x.AgreementCode)
            .NotEmpty().WithMessage("AgreementCode is required.")
            .Must(c => IntlAgreementValidatorShared.AgreementCodeRegex.IsMatch(c ?? string.Empty))
            .WithMessage("AgreementCode must match the pattern ^[A-Z]{2}_MD_\\d{4}$.");

        RuleFor(x => x.HostCountryCode)
            .NotEmpty().WithMessage("HostCountryCode is required.")
            .Must(c => IntlAgreementValidatorShared.HostCountryRegex.IsMatch(c ?? string.Empty))
            .WithMessage("HostCountryCode must be an ISO-3166 alpha-2 uppercase code (2 chars).");

        RuleFor(x => x.ReferenceBenefitPassportSqid!)
            .MaximumLength(IntlAgreementValidatorShared.ReferenceSqidMaxLength)
            .WithMessage($"ReferenceBenefitPassportSqid cannot exceed {IntlAgreementValidatorShared.ReferenceSqidMaxLength} characters.")
            .When(x => !string.IsNullOrEmpty(x.ReferenceBenefitPassportSqid));

        RuleFor(x => x.EvidenceJson!)
            .MaximumLength(IntlAgreementValidatorShared.EvidenceJsonMaxLength)
            .WithMessage($"EvidenceJson cannot exceed {IntlAgreementValidatorShared.EvidenceJsonMaxLength} characters.")
            .Must(IntlAgreementValidatorShared.IsValidJson)
            .WithMessage("EvidenceJson must be a valid JSON document.")
            .When(x => !string.IsNullOrEmpty(x.EvidenceJson));
    }
}

/// <summary>
/// R1201 / R1402 — validates <see cref="IntlAgreementReviewInputDto"/>.
/// Enforces outcome-enum membership and 3..2000-char note shape.
/// </summary>
public sealed class IntlAgreementReviewInputValidator
    : AbstractValidator<IntlAgreementReviewInputDto>
{
    /// <summary>Wires the rule set.</summary>
    public IntlAgreementReviewInputValidator()
    {
        RuleFor(x => x.Outcome)
            .NotEmpty().WithMessage("Outcome is required.")
            .Must(IntlAgreementValidatorShared.IsValidStepOutcome)
            .WithMessage("Outcome must be one of Approved, Rejected, RevisionRequested.");

        RuleFor(x => x.Note)
            .NotEmpty().WithMessage("Note is required.")
            .MinimumLength(IntlAgreementValidatorShared.NoteMinLength)
            .WithMessage($"Note must be at least {IntlAgreementValidatorShared.NoteMinLength} characters.")
            .MaximumLength(IntlAgreementValidatorShared.NoteMaxLength)
            .WithMessage($"Note cannot exceed {IntlAgreementValidatorShared.NoteMaxLength} characters.");
    }
}

/// <summary>
/// R1201 / R1402 — validates
/// <see cref="IntlAgreementReviewCaseResubmitInputDto"/>. Enforces note
/// length + evidence-JSON validity + size.
/// </summary>
public sealed class IntlAgreementReviewCaseResubmitInputValidator
    : AbstractValidator<IntlAgreementReviewCaseResubmitInputDto>
{
    /// <summary>Wires the rule set.</summary>
    public IntlAgreementReviewCaseResubmitInputValidator()
    {
        RuleFor(x => x.Note)
            .NotEmpty().WithMessage("Note is required.")
            .MinimumLength(IntlAgreementValidatorShared.NoteMinLength)
            .WithMessage($"Note must be at least {IntlAgreementValidatorShared.NoteMinLength} characters.")
            .MaximumLength(IntlAgreementValidatorShared.NoteMaxLength)
            .WithMessage($"Note cannot exceed {IntlAgreementValidatorShared.NoteMaxLength} characters.");

        RuleFor(x => x.EvidenceJson!)
            .MaximumLength(IntlAgreementValidatorShared.EvidenceJsonMaxLength)
            .WithMessage($"EvidenceJson cannot exceed {IntlAgreementValidatorShared.EvidenceJsonMaxLength} characters.")
            .Must(IntlAgreementValidatorShared.IsValidJson)
            .WithMessage("EvidenceJson must be a valid JSON document.")
            .When(x => !string.IsNullOrEmpty(x.EvidenceJson));
    }
}

/// <summary>
/// R1201 / R1402 — validates
/// <see cref="IntlAgreementReviewCaseReasonInputDto"/>. Enforces the
/// 3..2000-char reason shape.
/// </summary>
public sealed class IntlAgreementReviewCaseReasonInputValidator
    : AbstractValidator<IntlAgreementReviewCaseReasonInputDto>
{
    /// <summary>Wires the rule set.</summary>
    public IntlAgreementReviewCaseReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(IntlAgreementValidatorShared.NoteMinLength)
            .WithMessage($"Reason must be at least {IntlAgreementValidatorShared.NoteMinLength} characters.")
            .MaximumLength(IntlAgreementValidatorShared.NoteMaxLength)
            .WithMessage($"Reason cannot exceed {IntlAgreementValidatorShared.NoteMaxLength} characters.");
    }
}

/// <summary>
/// R1201 / R1402 — validates
/// <see cref="IntlAgreementReviewCaseFilterDto"/>. Enforces Skip / Take
/// bounds and rejects unknown enum names.
/// </summary>
public sealed class IntlAgreementReviewCaseFilterValidator
    : AbstractValidator<IntlAgreementReviewCaseFilterDto>
{
    /// <summary>Wires the rule set.</summary>
    public IntlAgreementReviewCaseFilterValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0).WithMessage("Skip must be >= 0.");

        RuleFor(x => x.Take)
            .InclusiveBetween(1, IntlAgreementValidatorShared.MaxTake)
            .WithMessage($"Take must be between 1 and {IntlAgreementValidatorShared.MaxTake}.");

        RuleFor(x => x.Status!)
            .Must(IntlAgreementValidatorShared.IsValidStatus)
            .WithMessage("Status must be a known IntlAgreementReviewCaseStatus enum name.")
            .When(x => !string.IsNullOrEmpty(x.Status));

        RuleFor(x => x.BenefitKind!)
            .Must(IntlAgreementValidatorShared.IsValidBenefitKind)
            .WithMessage("BenefitKind must be a known IntlAgreementBenefitKind enum name.")
            .When(x => !string.IsNullOrEmpty(x.BenefitKind));

        RuleFor(x => x.CurrentLevel!)
            .Must(IntlAgreementValidatorShared.IsValidLevel)
            .WithMessage("CurrentLevel must be a known IntlAgreementReviewLevel enum name.")
            .When(x => !string.IsNullOrEmpty(x.CurrentLevel));

        RuleFor(x => x.AgreementCode!)
            .Must(c => IntlAgreementValidatorShared.AgreementCodeRegex.IsMatch(c ?? string.Empty))
            .WithMessage("AgreementCode must match the pattern ^[A-Z]{2}_MD_\\d{4}$.")
            .When(x => !string.IsNullOrEmpty(x.AgreementCode));

        RuleFor(x => x.HostCountryCode!)
            .Must(c => IntlAgreementValidatorShared.HostCountryRegex.IsMatch(c ?? string.Empty))
            .WithMessage("HostCountryCode must be an ISO-3166 alpha-2 uppercase code (2 chars).")
            .When(x => !string.IsNullOrEmpty(x.HostCountryCode));
    }
}
