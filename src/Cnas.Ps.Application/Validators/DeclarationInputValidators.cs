using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0810 / R0811 / R0812 / R0813 — FluentValidation rules for the declaration
/// input DTOs. The shared rule set is factored out so each input-specific
/// validator only differs in the <c>Kind</c> allow-list.
/// </summary>
internal static class DeclarationValidatorShared
{
    /// <summary>Maximum permitted gross contribution amount (MDL).</summary>
    public const decimal MaxContributionAmount = 100_000_000m;

    /// <summary>Minimum permitted reason length for adjust / cancel operations.</summary>
    public const int ReasonMinLength = 3;

    /// <summary>Maximum permitted reason / notes length.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>Maximum permitted external reference-number length.</summary>
    public const int ReferenceMaxLength = 64;

    /// <summary>
    /// Asserts the reporting month carries <c>Day == 1</c> per the
    /// <see cref="Declaration.ReportingMonth"/> contract.
    /// </summary>
    /// <param name="month">Candidate month.</param>
    /// <returns><c>true</c> when the day component is 1.</returns>
    public static bool IsFirstOfMonth(DateOnly month) => month.Day == 1;

    /// <summary>
    /// Asserts <paramref name="value"/> matches one of the supplied enum names
    /// (case-sensitive).
    /// </summary>
    /// <param name="value">Candidate enum-name string.</param>
    /// <param name="allowed">Allow-list of canonical enum names.</param>
    /// <returns><c>true</c> when the value is non-null and present in the allow-list.</returns>
    public static bool IsKindIn(string? value, IReadOnlyCollection<string> allowed)
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
/// R0810 / BP 1.2-A — validates <see cref="DeclarationFromSfsInputDto"/>. The
/// <c>ReferenceNumber</c> is required for SFS rows because the upstream feed
/// always carries one.
/// </summary>
public sealed class DeclarationFromSfsInputDtoValidator : AbstractValidator<DeclarationFromSfsInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public DeclarationFromSfsInputDtoValidator()
    {
        RuleFor(x => x.ContributorSqid)
            .NotEmpty().WithMessage("ContributorSqid is required.");

        RuleFor(x => x.ReportingMonth)
            .Must(DeclarationValidatorShared.IsFirstOfMonth)
            .WithMessage("ReportingMonth must be the first day of the month (Day == 1).");

        RuleFor(x => x.ReferenceNumber)
            .NotEmpty().WithMessage("ReferenceNumber is required for SFS declarations.")
            .MaximumLength(DeclarationValidatorShared.ReferenceMaxLength)
            .WithMessage($"ReferenceNumber cannot exceed {DeclarationValidatorShared.ReferenceMaxLength} characters.");

        RuleFor(x => x.DeclaredContributionAmount)
            .GreaterThanOrEqualTo(0m)
            .WithMessage("DeclaredContributionAmount must be ≥ 0.")
            .LessThanOrEqualTo(DeclarationValidatorShared.MaxContributionAmount)
            .WithMessage($"DeclaredContributionAmount cannot exceed {DeclarationValidatorShared.MaxContributionAmount:0}.");

        RuleFor(x => x.Notes!)
            .MinimumLength(DeclarationValidatorShared.ReasonMinLength)
            .MaximumLength(DeclarationValidatorShared.ReasonMaxLength)
            .When(x => x.Notes is not null)
            .WithMessage($"Notes must be {DeclarationValidatorShared.ReasonMinLength}..{DeclarationValidatorShared.ReasonMaxLength} characters when supplied.");
    }
}

/// <summary>
/// R0811 / BP 1.2-B — validates <see cref="DeclarationAtCnasInputDto"/>. The
/// <see cref="DeclarationAtCnasInputDto.Kind"/> is restricted to
/// <c>{BassFour, Bass, BassAn, Pre2018}</c>.
/// </summary>
public sealed class DeclarationAtCnasInputDtoValidator : AbstractValidator<DeclarationAtCnasInputDto>
{
    /// <summary>Canonical enum names permitted on the CNAS-desk endpoint.</summary>
    private static readonly IReadOnlyCollection<string> AllowedKinds =
    [
        nameof(DeclarationKind.BassFour),
        nameof(DeclarationKind.Bass),
        nameof(DeclarationKind.BassAn),
        nameof(DeclarationKind.Pre2018),
    ];

    /// <summary>Builds the rule set.</summary>
    public DeclarationAtCnasInputDtoValidator()
    {
        RuleFor(x => x.ContributorSqid)
            .NotEmpty().WithMessage("ContributorSqid is required.");

        RuleFor(x => x.Kind)
            .Must(k => DeclarationValidatorShared.IsKindIn(k, AllowedKinds))
            .WithMessage("Kind must be one of BassFour, Bass, BassAn, Pre2018.");

        RuleFor(x => x.ReportingMonth)
            .Must(DeclarationValidatorShared.IsFirstOfMonth)
            .WithMessage("ReportingMonth must be the first day of the month (Day == 1).");

        RuleFor(x => x.ReferenceNumber!)
            .MinimumLength(1)
            .MaximumLength(DeclarationValidatorShared.ReferenceMaxLength)
            .When(x => x.ReferenceNumber is not null)
            .WithMessage($"ReferenceNumber must be 1..{DeclarationValidatorShared.ReferenceMaxLength} characters when supplied.");

        RuleFor(x => x.DeclaredContributionAmount)
            .GreaterThanOrEqualTo(0m)
            .WithMessage("DeclaredContributionAmount must be ≥ 0.")
            .LessThanOrEqualTo(DeclarationValidatorShared.MaxContributionAmount)
            .WithMessage($"DeclaredContributionAmount cannot exceed {DeclarationValidatorShared.MaxContributionAmount:0}.");

        RuleFor(x => x.Notes!)
            .MinimumLength(DeclarationValidatorShared.ReasonMinLength)
            .MaximumLength(DeclarationValidatorShared.ReasonMaxLength)
            .When(x => x.Notes is not null)
            .WithMessage($"Notes must be {DeclarationValidatorShared.ReasonMinLength}..{DeclarationValidatorShared.ReasonMaxLength} characters when supplied.");
    }
}

/// <summary>
/// R0812 / BP 1.2-C — validates <see cref="DeclarationFromOtherDocumentInputDto"/>.
/// The <see cref="DeclarationFromOtherDocumentInputDto.Kind"/> is restricted to
/// <c>{Control, CourtDecision, Other}</c>.
/// </summary>
public sealed class DeclarationFromOtherDocumentInputDtoValidator
    : AbstractValidator<DeclarationFromOtherDocumentInputDto>
{
    /// <summary>Canonical enum names permitted on the "other document" endpoint.</summary>
    private static readonly IReadOnlyCollection<string> AllowedKinds =
    [
        nameof(DeclarationKind.Control),
        nameof(DeclarationKind.CourtDecision),
        nameof(DeclarationKind.Other),
    ];

    /// <summary>Builds the rule set.</summary>
    public DeclarationFromOtherDocumentInputDtoValidator()
    {
        RuleFor(x => x.ContributorSqid)
            .NotEmpty().WithMessage("ContributorSqid is required.");

        RuleFor(x => x.Kind)
            .Must(k => DeclarationValidatorShared.IsKindIn(k, AllowedKinds))
            .WithMessage("Kind must be one of Control, CourtDecision, Other.");

        RuleFor(x => x.ReportingMonth)
            .Must(DeclarationValidatorShared.IsFirstOfMonth)
            .WithMessage("ReportingMonth must be the first day of the month (Day == 1).");

        RuleFor(x => x.ReferenceNumber!)
            .MinimumLength(1)
            .MaximumLength(DeclarationValidatorShared.ReferenceMaxLength)
            .When(x => x.ReferenceNumber is not null)
            .WithMessage($"ReferenceNumber must be 1..{DeclarationValidatorShared.ReferenceMaxLength} characters when supplied.");

        RuleFor(x => x.DeclaredContributionAmount)
            .GreaterThanOrEqualTo(0m)
            .WithMessage("DeclaredContributionAmount must be ≥ 0.")
            .LessThanOrEqualTo(DeclarationValidatorShared.MaxContributionAmount)
            .WithMessage($"DeclaredContributionAmount cannot exceed {DeclarationValidatorShared.MaxContributionAmount:0}.");

        RuleFor(x => x.Notes!)
            .MinimumLength(DeclarationValidatorShared.ReasonMinLength)
            .MaximumLength(DeclarationValidatorShared.ReasonMaxLength)
            .When(x => x.Notes is not null)
            .WithMessage($"Notes must be {DeclarationValidatorShared.ReasonMinLength}..{DeclarationValidatorShared.ReasonMaxLength} characters when supplied.");
    }
}

/// <summary>
/// R0810-R0812 — validates <see cref="DeclarationAdjustInputDto"/>. Reason
/// 3..500 chars; new amount within the same bounds as the declared figure.
/// </summary>
public sealed class DeclarationAdjustInputDtoValidator : AbstractValidator<DeclarationAdjustInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public DeclarationAdjustInputDtoValidator()
    {
        RuleFor(x => x.AdjustedAmount)
            .GreaterThanOrEqualTo(0m)
            .WithMessage("AdjustedAmount must be ≥ 0.")
            .LessThanOrEqualTo(DeclarationValidatorShared.MaxContributionAmount)
            .WithMessage($"AdjustedAmount cannot exceed {DeclarationValidatorShared.MaxContributionAmount:0}.");

        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(DeclarationValidatorShared.ReasonMinLength)
            .WithMessage($"Reason must be at least {DeclarationValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(DeclarationValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason cannot exceed {DeclarationValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>R0810-R0812 — validates <see cref="DeclarationCancelInputDto"/>.</summary>
public sealed class DeclarationCancelInputDtoValidator : AbstractValidator<DeclarationCancelInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public DeclarationCancelInputDtoValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(DeclarationValidatorShared.ReasonMinLength)
            .WithMessage($"Reason must be at least {DeclarationValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(DeclarationValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason cannot exceed {DeclarationValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R0821 / BP 1.2-L — validates
/// <see cref="ScannedDeclarationAttachmentInputDto"/>. The blob itself is
/// sniffed by <c>IAttachmentValidator</c>; this validator only enforces the
/// declaration-specific envelope rules (filename + OCR metadata caps).
/// </summary>
public sealed class ScannedDeclarationAttachmentInputDtoValidator
    : AbstractValidator<ScannedDeclarationAttachmentInputDto>
{
    /// <summary>Maximum permitted OCR-extracted JSON payload (characters).</summary>
    public const int MaxOcrExtractedJsonLength = 100_000;

    /// <summary>Canonical confidence-band literals accepted on the wire.</summary>
    public static readonly IReadOnlyCollection<string> AllowedConfidenceLevels =
    [
        "High",
        "Medium",
        "Low",
    ];

    /// <summary>Builds the rule set.</summary>
    public ScannedDeclarationAttachmentInputDtoValidator()
    {
        RuleFor(x => x.FileBase64)
            .NotEmpty().WithMessage("FileBase64 is required.");

        RuleFor(x => x.FileName)
            .NotEmpty().WithMessage("FileName is required.")
            .MaximumLength(255).WithMessage("FileName cannot exceed 255 characters.");

        RuleFor(x => x.OcrExtractedJson!)
            .MaximumLength(MaxOcrExtractedJsonLength)
            .When(x => x.OcrExtractedJson is not null)
            .WithMessage($"OcrExtractedJson cannot exceed {MaxOcrExtractedJsonLength:N0} characters.");

        RuleFor(x => x.OcrConfidenceLevel!)
            .Must(level => DeclarationValidatorShared.IsKindIn(level, AllowedConfidenceLevels))
            .When(x => x.OcrConfidenceLevel is not null)
            .WithMessage("OcrConfidenceLevel must be one of High, Medium, Low.");
    }
}

/// <summary>
/// R0822 / BP 1.2-M — validates the paging + window slots on
/// <see cref="DeclarationsSearchInput"/>. The QBE envelope itself is validated
/// downstream by <see cref="QbeFilterDtoValidator"/>; this validator only
/// enforces the paging / window caps so the explorer endpoint cannot be tricked
/// into materialising more than <c>MaxTake</c> rows in a single round-trip.
/// </summary>
public sealed class DeclarationsSearchInputValidator : AbstractValidator<DeclarationsSearchInput>
{
    /// <summary>Maximum permitted page size (matches the budget-gate cap).</summary>
    public const int MaxTake = 200;

    /// <summary>Builds the rule set.</summary>
    public DeclarationsSearchInputValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Skip must be ≥ 0.");

        RuleFor(x => x.Take)
            .GreaterThan(0)
            .WithMessage("Take must be > 0.")
            .LessThanOrEqualTo(MaxTake)
            .WithMessage($"Take cannot exceed {MaxTake}.");
    }
}
