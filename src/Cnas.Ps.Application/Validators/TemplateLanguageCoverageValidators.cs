using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R2003 / R0133 — shared constants for the template-coverage validators.
/// Centralised so the magic numbers stay in lock-step across the filter +
/// findings-filter + acknowledgement rule sets.
/// </summary>
internal static class TemplateLanguageCoverageValidatorShared
{
    /// <summary>Maximum permitted <c>Take</c> on the coverage report endpoint.</summary>
    public const int ReportTakeMaxPageSize = 500;

    /// <summary>Maximum permitted <c>Take</c> on the findings-list endpoint.</summary>
    public const int FindingsTakeMaxPageSize = 200;

    /// <summary>Maximum number of distinct required-language codes per filter envelope.</summary>
    public const int RequiredLanguagesMaxCount = 10;

    /// <summary>Minimum acknowledgement-note length.</summary>
    public const int NoteMinLength = 3;

    /// <summary>Maximum acknowledgement-note length.</summary>
    public const int NoteMaxLength = 1000;

    /// <summary>
    /// Regex enforcing the lowercase ISO 639-1 / 639-2 code shape — two or
    /// three lowercase ASCII letters (e.g. <c>"ro"</c>, <c>"en"</c>,
    /// <c>"ru"</c>, <c>"ukr"</c>). Compiled at class load so re-use is
    /// allocation-free.
    /// </summary>
    public static readonly Regex LanguageCodeRegex = new(
        "^[a-z]{2,3}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}

/// <summary>
/// R2003 — validates <see cref="TemplateLanguageCoverageFilterDto"/>. Enforces
/// page bounds, each required-language code shape, and the maximum number of
/// distinct required-language codes.
/// </summary>
public sealed class TemplateLanguageCoverageFilterValidator
    : AbstractValidator<TemplateLanguageCoverageFilterDto>
{
    /// <summary>Builds the rule set.</summary>
    public TemplateLanguageCoverageFilterValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Skip must be >= 0.");

        RuleFor(x => x.Take)
            .GreaterThanOrEqualTo(1)
            .LessThanOrEqualTo(TemplateLanguageCoverageValidatorShared.ReportTakeMaxPageSize)
            .WithMessage(
                $"Take must be in 1..{TemplateLanguageCoverageValidatorShared.ReportTakeMaxPageSize}.");

        RuleFor(x => x.RequiredLanguages!)
            .Must(list => list is null
                || list.Count <= TemplateLanguageCoverageValidatorShared.RequiredLanguagesMaxCount)
            .WithMessage(
                $"RequiredLanguages cannot exceed {TemplateLanguageCoverageValidatorShared.RequiredLanguagesMaxCount} entries.")
            .When(x => x.RequiredLanguages is not null);

        RuleForEach(x => x.RequiredLanguages!)
            .Must(code => code is not null
                && TemplateLanguageCoverageValidatorShared.LanguageCodeRegex.IsMatch(code))
            .WithMessage("RequiredLanguages entries must match ^[a-z]{2,3}$.")
            .When(x => x.RequiredLanguages is not null);
    }
}

/// <summary>
/// R2003 — validates <see cref="TemplateLanguageCoverageFindingFilterDto"/>.
/// Enforces page bounds and the optional MissingLanguage code shape.
/// </summary>
public sealed class TemplateLanguageCoverageFindingFilterValidator
    : AbstractValidator<TemplateLanguageCoverageFindingFilterDto>
{
    /// <summary>Builds the rule set.</summary>
    public TemplateLanguageCoverageFindingFilterValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Skip must be >= 0.");

        RuleFor(x => x.Take)
            .GreaterThanOrEqualTo(1)
            .LessThanOrEqualTo(TemplateLanguageCoverageValidatorShared.FindingsTakeMaxPageSize)
            .WithMessage(
                $"Take must be in 1..{TemplateLanguageCoverageValidatorShared.FindingsTakeMaxPageSize}.");

        RuleFor(x => x.MissingLanguage!)
            .Must(code => TemplateLanguageCoverageValidatorShared.LanguageCodeRegex.IsMatch(code))
            .When(x => !string.IsNullOrEmpty(x.MissingLanguage))
            .WithMessage("MissingLanguage must match ^[a-z]{2,3}$ when set.");
    }
}

/// <summary>
/// R2003 — validates <see cref="TemplateLanguageCoverageAcknowledgeInputDto"/>.
/// The note must be present and 3..1000 chars (matches the SCHEMA cap).
/// </summary>
public sealed class TemplateLanguageCoverageAcknowledgeInputValidator
    : AbstractValidator<TemplateLanguageCoverageAcknowledgeInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public TemplateLanguageCoverageAcknowledgeInputValidator()
    {
        RuleFor(x => x.Note)
            .NotEmpty().WithMessage("Note is required.")
            .MinimumLength(TemplateLanguageCoverageValidatorShared.NoteMinLength)
            .WithMessage(
                $"Note must be at least {TemplateLanguageCoverageValidatorShared.NoteMinLength} characters.")
            .MaximumLength(TemplateLanguageCoverageValidatorShared.NoteMaxLength)
            .WithMessage(
                $"Note cannot exceed {TemplateLanguageCoverageValidatorShared.NoteMaxLength} characters.");
    }
}
