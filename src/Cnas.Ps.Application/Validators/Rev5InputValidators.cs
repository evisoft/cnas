using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0910 / R0913 — shared constants for the REV-5 + insured-person-adjustment
/// validators. Centralised so the magic numbers don't drift across the rule
/// sets.
/// </summary>
internal static class Rev5ValidatorShared
{
    /// <summary>Maximum permitted contribution amount per row (MDL).</summary>
    public const decimal MaxContributionAmount = 100_000_000m;

    /// <summary>Maximum permitted signed adjustment magnitude (MDL).</summary>
    public const decimal MaxAdjustmentMagnitude = 10_000_000m;

    /// <summary>Maximum permitted external reference-number length.</summary>
    public const int ReferenceMaxLength = 64;

    /// <summary>Maximum permitted source-document reference length.</summary>
    public const int SourceDocumentReferenceMaxLength = 128;

    /// <summary>Maximum permitted IDNP-hash length.</summary>
    public const int NationalIdHashMaxLength = 128;

    /// <summary>Minimum permitted reason length.</summary>
    public const int ReasonMinLength = 3;

    /// <summary>Maximum permitted reason / notes length.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>Maximum permitted position-code length.</summary>
    public const int PositionCodeMaxLength = 64;

    /// <summary>Maximum number of child rows per REV-5 declaration.</summary>
    public const int MaxRowsPerDeclaration = 50_000;

    /// <summary>
    /// Allow-list of stable source-document codes accepted by R0913
    /// adjustments. Kept private to the validators so the canonical list has
    /// exactly one source of truth.
    /// </summary>
    public static readonly IReadOnlyCollection<string> AllowedSourceDocumentCodes =
    [
        "CourtDecision",
        "AdminControl",
        "IndividualContract",
        "Other",
    ];

    /// <summary>
    /// Asserts the reporting month carries <c>Day == 1</c> per the entity
    /// contracts.
    /// </summary>
    /// <param name="month">Candidate month.</param>
    /// <returns><c>true</c> when the day component is 1.</returns>
    public static bool IsFirstOfMonth(DateOnly month) => month.Day == 1;

    /// <summary>
    /// Asserts <paramref name="value"/> matches one of the supplied stable
    /// codes (case-sensitive ordinal comparison).
    /// </summary>
    /// <param name="value">Candidate code.</param>
    /// <param name="allowed">Allow-list of canonical codes.</param>
    /// <returns><c>true</c> when the value is non-null and present in the allow-list.</returns>
    public static bool IsCodeIn(string? value, IReadOnlyCollection<string> allowed)
    {
        if (value is null)
        {
            return false;
        }
        foreach (var candidate in allowed)
        {
            if (string.Equals(candidate, value, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// R0910 / BP 2.2-A — validates <see cref="Rev5DeclarationRowInputDto"/>.
/// Constrains the IDNP-hash length, the money fields, and the optional
/// context fields.
/// </summary>
public sealed class Rev5DeclarationRowInputDtoValidator : AbstractValidator<Rev5DeclarationRowInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public Rev5DeclarationRowInputDtoValidator()
    {
        RuleFor(x => x.InsuredPersonNationalIdHash)
            .NotEmpty().WithMessage("InsuredPersonNationalIdHash is required.")
            .MinimumLength(1)
            .MaximumLength(Rev5ValidatorShared.NationalIdHashMaxLength)
            .WithMessage($"InsuredPersonNationalIdHash must be 1..{Rev5ValidatorShared.NationalIdHashMaxLength} characters.");

        RuleFor(x => x.ContributionBaseAmount)
            .GreaterThanOrEqualTo(0m)
            .WithMessage("ContributionBaseAmount must be ≥ 0.")
            .LessThanOrEqualTo(Rev5ValidatorShared.MaxContributionAmount)
            .WithMessage($"ContributionBaseAmount cannot exceed {Rev5ValidatorShared.MaxContributionAmount:0}.");

        RuleFor(x => x.ContributionAmount)
            .GreaterThanOrEqualTo(0m)
            .WithMessage("ContributionAmount must be ≥ 0.")
            .LessThanOrEqualTo(Rev5ValidatorShared.MaxContributionAmount)
            .WithMessage($"ContributionAmount cannot exceed {Rev5ValidatorShared.MaxContributionAmount:0}.");

        RuleFor(x => x.DaysWorked!.Value)
            .InclusiveBetween(0, 31)
            .When(x => x.DaysWorked is not null)
            .WithMessage("DaysWorked must be 0..31 when supplied.");

        RuleFor(x => x.PositionCode!)
            .MaximumLength(Rev5ValidatorShared.PositionCodeMaxLength)
            .When(x => x.PositionCode is not null)
            .WithMessage($"PositionCode cannot exceed {Rev5ValidatorShared.PositionCodeMaxLength} characters.");
    }
}

/// <summary>
/// R0910 / BP 2.2-A — validates <see cref="Rev5DeclarationRegisterInputDto"/>.
/// Enforces the natural-key inputs (employer Sqid, first-of-month, reference
/// number length), the row-count bounds, and recursively validates each child
/// row via the row validator.
/// </summary>
public sealed class Rev5DeclarationRegisterInputDtoValidator
    : AbstractValidator<Rev5DeclarationRegisterInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public Rev5DeclarationRegisterInputDtoValidator()
    {
        RuleFor(x => x.FilingContributorSqid)
            .NotEmpty().WithMessage("FilingContributorSqid is required.");

        RuleFor(x => x.ReportingMonth)
            .Must(Rev5ValidatorShared.IsFirstOfMonth)
            .WithMessage("ReportingMonth must be the first day of the month (Day == 1).");

        RuleFor(x => x.ReferenceNumber)
            .NotEmpty().WithMessage("ReferenceNumber is required.")
            .MinimumLength(1)
            .MaximumLength(Rev5ValidatorShared.ReferenceMaxLength)
            .WithMessage($"ReferenceNumber must be 1..{Rev5ValidatorShared.ReferenceMaxLength} characters.");

        RuleFor(x => x.Rows)
            .NotNull().WithMessage("Rows is required.")
            .Must(rows => rows is not null && rows.Count >= 1)
            .WithMessage("Rows must contain at least one entry.")
            .Must(rows => rows is null || rows.Count <= Rev5ValidatorShared.MaxRowsPerDeclaration)
            .WithMessage($"Rows cannot exceed {Rev5ValidatorShared.MaxRowsPerDeclaration} entries.");

        RuleForEach(x => x.Rows).SetValidator(new Rev5DeclarationRowInputDtoValidator());

        RuleFor(x => x.Notes!)
            .MaximumLength(Rev5ValidatorShared.ReasonMaxLength)
            .When(x => x.Notes is not null)
            .WithMessage($"Notes cannot exceed {Rev5ValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R0910 — validates <see cref="Rev5DeclarationRowAdjustInputDto"/>.
/// </summary>
public sealed class Rev5DeclarationRowAdjustInputDtoValidator
    : AbstractValidator<Rev5DeclarationRowAdjustInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public Rev5DeclarationRowAdjustInputDtoValidator()
    {
        RuleFor(x => x.AdjustedContributionAmount)
            .GreaterThanOrEqualTo(0m)
            .WithMessage("AdjustedContributionAmount must be ≥ 0.")
            .LessThanOrEqualTo(Rev5ValidatorShared.MaxContributionAmount)
            .WithMessage($"AdjustedContributionAmount cannot exceed {Rev5ValidatorShared.MaxContributionAmount:0}.");

        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(Rev5ValidatorShared.ReasonMinLength)
            .MaximumLength(Rev5ValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason must be {Rev5ValidatorShared.ReasonMinLength}..{Rev5ValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R0910 — validates <see cref="Rev5DeclarationCancelInputDto"/>.
/// </summary>
public sealed class Rev5DeclarationCancelInputDtoValidator
    : AbstractValidator<Rev5DeclarationCancelInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public Rev5DeclarationCancelInputDtoValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(Rev5ValidatorShared.ReasonMinLength)
            .MaximumLength(Rev5ValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason must be {Rev5ValidatorShared.ReasonMinLength}..{Rev5ValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R0913 / BP 2.2-D — validates
/// <see cref="InsuredPersonContributionAdjustmentInputDto"/>. Constrains the
/// signed adjustment magnitude, the first-of-month rule, and the
/// source-document allow-list.
/// </summary>
public sealed class InsuredPersonContributionAdjustmentInputDtoValidator
    : AbstractValidator<InsuredPersonContributionAdjustmentInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public InsuredPersonContributionAdjustmentInputDtoValidator()
    {
        RuleFor(x => x.InsuredPersonSolicitantSqid)
            .NotEmpty().WithMessage("InsuredPersonSolicitantSqid is required.");

        RuleFor(x => x.Month)
            .Must(Rev5ValidatorShared.IsFirstOfMonth)
            .WithMessage("Month must be the first day of the month (Day == 1).");

        RuleFor(x => x.AdjustmentAmount)
            .GreaterThanOrEqualTo(-Rev5ValidatorShared.MaxAdjustmentMagnitude)
            .LessThanOrEqualTo(Rev5ValidatorShared.MaxAdjustmentMagnitude)
            .WithMessage($"AdjustmentAmount must be within ±{Rev5ValidatorShared.MaxAdjustmentMagnitude:0}.");

        RuleFor(x => x.SourceDocumentCode)
            .Must(c => Rev5ValidatorShared.IsCodeIn(c, Rev5ValidatorShared.AllowedSourceDocumentCodes))
            .WithMessage("SourceDocumentCode must be one of CourtDecision, AdminControl, IndividualContract, Other.");

        RuleFor(x => x.SourceDocumentReference!)
            .MaximumLength(Rev5ValidatorShared.SourceDocumentReferenceMaxLength)
            .When(x => x.SourceDocumentReference is not null)
            .WithMessage($"SourceDocumentReference cannot exceed {Rev5ValidatorShared.SourceDocumentReferenceMaxLength} characters.");

        RuleFor(x => x.Reason!)
            .MinimumLength(Rev5ValidatorShared.ReasonMinLength)
            .MaximumLength(Rev5ValidatorShared.ReasonMaxLength)
            .When(x => x.Reason is not null)
            .WithMessage($"Reason must be {Rev5ValidatorShared.ReasonMinLength}..{Rev5ValidatorShared.ReasonMaxLength} characters when supplied.");
    }
}
