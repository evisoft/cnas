using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R2282 / TOR SEC 036 — shared helpers + constants for the integrity-check
/// validators. Centralised so the regex / range bounds don't drift across
/// rule sets.
/// </summary>
internal static partial class IntegrityCheckValidatorShared
{
    /// <summary>Maximum permitted aggregate-name length.</summary>
    public const int AggregateNameMaxLength = 128;

    /// <summary>Maximum permitted check-code length (matches the entity's column cap).</summary>
    public const int CheckCodeMaxLength = 64;

    /// <summary>Minimum acknowledgement-note length.</summary>
    public const int NoteMinLength = 3;

    /// <summary>Maximum acknowledgement-note length.</summary>
    public const int NoteMaxLength = 1000;

    /// <summary>Maximum permitted <c>Take</c> on the open-findings list.</summary>
    public const int MaxTake = 200;

    /// <summary>
    /// Check-code shape — uppercase ASCII letters / digits / underscore / dot,
    /// starting with a letter, 2..64 chars (entity column cap is 64).
    /// </summary>
    [GeneratedRegex(@"^[A-Z][A-Z0-9_.]{1,63}$", RegexOptions.CultureInvariant)]
    public static partial Regex CheckCodeRegex();

    /// <summary>True when the supplied severity string parses to a valid <see cref="IntegrityFindingSeverity"/> name.</summary>
    /// <param name="severity">Candidate severity name (case-sensitive).</param>
    /// <returns><c>true</c> when the value parses; null is accepted as "no filter".</returns>
    public static bool SeverityIsValidOrNull(string? severity)
        => severity is null
            || Enum.TryParse<IntegrityFindingSeverity>(severity, ignoreCase: false, out _);

    /// <summary>True when the supplied check code is null OR matches the canonical shape.</summary>
    /// <param name="checkCode">Candidate check code.</param>
    /// <returns><c>true</c> when null or shape-conformant.</returns>
    public static bool CheckCodeIsValidOrNull(string? checkCode)
        => checkCode is null || CheckCodeRegex().IsMatch(checkCode);
}

/// <summary>
/// R2282 / TOR SEC 036 — validates <see cref="IntegrityFindingFilterDto"/>.
/// Enforces the severity-name parse, the aggregate-name / check-code length
/// caps, and the Skip/Take page bounds.
/// </summary>
public sealed class IntegrityFindingFilterValidator : AbstractValidator<IntegrityFindingFilterDto>
{
    /// <summary>Builds the rule set.</summary>
    public IntegrityFindingFilterValidator()
    {
        RuleFor(x => x.Severity)
            .Must(IntegrityCheckValidatorShared.SeverityIsValidOrNull)
            .WithMessage("Severity must be one of: Critical, High, Medium, Low — or null to match any.");

        RuleFor(x => x.AggregateName!)
            .MaximumLength(IntegrityCheckValidatorShared.AggregateNameMaxLength)
            .When(x => x.AggregateName is not null)
            .WithMessage($"AggregateName cannot exceed {IntegrityCheckValidatorShared.AggregateNameMaxLength} characters.");

        RuleFor(x => x.CheckCode)
            .Must(IntegrityCheckValidatorShared.CheckCodeIsValidOrNull)
            .WithMessage("CheckCode must be SCREAMING_SNAKE_CASE matching ^[A-Z][A-Z0-9_.]{1,63}$.");

        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Skip must be >= 0.");

        RuleFor(x => x.Take)
            .GreaterThanOrEqualTo(1)
            .LessThanOrEqualTo(IntegrityCheckValidatorShared.MaxTake)
            .WithMessage($"Take must be in 1..{IntegrityCheckValidatorShared.MaxTake}.");
    }
}

/// <summary>
/// R2282 / TOR SEC 036 — validates <see cref="IntegrityFindingAcknowledgeInputDto"/>.
/// The note must be present and 3..1000 chars.
/// </summary>
public sealed class IntegrityFindingAcknowledgeInputValidator
    : AbstractValidator<IntegrityFindingAcknowledgeInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public IntegrityFindingAcknowledgeInputValidator()
    {
        RuleFor(x => x.Note)
            .NotEmpty().WithMessage("Note is required.")
            .MinimumLength(IntegrityCheckValidatorShared.NoteMinLength)
            .WithMessage($"Note must be at least {IntegrityCheckValidatorShared.NoteMinLength} characters.")
            .MaximumLength(IntegrityCheckValidatorShared.NoteMaxLength)
            .WithMessage($"Note cannot exceed {IntegrityCheckValidatorShared.NoteMaxLength} characters.");
    }
}
