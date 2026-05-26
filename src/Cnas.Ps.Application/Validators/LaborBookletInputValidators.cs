using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0920 / R0921 — FluentValidation rules shared across the labor-booklet and
/// pre-1999 period input DTOs.
/// </summary>
internal static partial class LaborBookletValidatorShared
{
    /// <summary>Minimum permitted reason length for verifier / rejection notes.</summary>
    public const int ReasonMinLength = 3;

    /// <summary>Maximum permitted reason / notes length.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>Maximum permitted booklet-number length.</summary>
    public const int CarnetMuncaNumberMaxLength = 32;

    /// <summary>Maximum permitted employer / position name length.</summary>
    public const int NameMaxLength = 200;

    /// <summary>Maximum permitted proof-document-reference length.</summary>
    public const int ProofDocumentReferenceMaxLength = 200;

    /// <summary>Maximum permitted OCR-extracted JSON payload length (characters).</summary>
    public const int MaxOcrExtractedJsonLength = 100_000;

    /// <summary>
    /// Last calendar day before the 01.01.1999 transition above which the
    /// regular contribution-declarations pipeline (R0810-R0823) takes over.
    /// Periods that end ON OR BEFORE this date are pre-1999.
    /// </summary>
    public static readonly DateOnly Pre1999CutOff = new(1998, 12, 31);

    /// <summary>Canonical OCR confidence-band literals accepted on the wire.</summary>
    public static readonly IReadOnlyCollection<string> AllowedConfidenceLevels =
    [
        "High",
        "Medium",
        "Low",
    ];

    /// <summary>
    /// Compiled regex enforcing the booklet-number alphabet:
    /// <c>^[A-Z0-9-]+$</c> (uppercase letters, digits, dash).
    /// </summary>
    /// <returns>Compiled regex instance.</returns>
    [GeneratedRegex("^[A-Z0-9-]+$", RegexOptions.CultureInvariant)]
    public static partial Regex CarnetMuncaNumberPattern();

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

/// <summary>R0920 — validates <see cref="LaborBookletRegisterInputDto"/>.</summary>
public sealed class LaborBookletRegisterInputDtoValidator
    : AbstractValidator<LaborBookletRegisterInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public LaborBookletRegisterInputDtoValidator()
    {
        RuleFor(x => x.InsuredPersonSqid)
            .NotEmpty().WithMessage("InsuredPersonSqid is required.");

        RuleFor(x => x.CarnetMuncaNumber)
            .NotEmpty().WithMessage("CarnetMuncaNumber is required.")
            .MaximumLength(LaborBookletValidatorShared.CarnetMuncaNumberMaxLength)
            .WithMessage($"CarnetMuncaNumber cannot exceed {LaborBookletValidatorShared.CarnetMuncaNumberMaxLength} characters.")
            .Must(n => n is not null && LaborBookletValidatorShared.CarnetMuncaNumberPattern().IsMatch(n))
            .WithMessage("CarnetMuncaNumber must match the pattern ^[A-Z0-9-]+$ (uppercase letters, digits, dash).");

        RuleFor(x => x.IssuingAuthority!)
            .MinimumLength(1)
            .MaximumLength(LaborBookletValidatorShared.NameMaxLength)
            .When(x => x.IssuingAuthority is not null)
            .WithMessage($"IssuingAuthority must be 1..{LaborBookletValidatorShared.NameMaxLength} characters when supplied.");

        RuleFor(x => x.Notes!)
            .MinimumLength(LaborBookletValidatorShared.ReasonMinLength)
            .MaximumLength(LaborBookletValidatorShared.ReasonMaxLength)
            .When(x => x.Notes is not null)
            .WithMessage($"Notes must be {LaborBookletValidatorShared.ReasonMinLength}..{LaborBookletValidatorShared.ReasonMaxLength} characters when supplied.");
    }
}

/// <summary>R0920 — validates <see cref="LaborBookletVerifyInputDto"/>.</summary>
public sealed class LaborBookletVerifyInputDtoValidator
    : AbstractValidator<LaborBookletVerifyInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public LaborBookletVerifyInputDtoValidator()
    {
        RuleFor(x => x.Notes!)
            .MinimumLength(LaborBookletValidatorShared.ReasonMinLength)
            .MaximumLength(LaborBookletValidatorShared.ReasonMaxLength)
            .When(x => x.Notes is not null)
            .WithMessage($"Notes must be {LaborBookletValidatorShared.ReasonMinLength}..{LaborBookletValidatorShared.ReasonMaxLength} characters when supplied.");
    }
}

/// <summary>R0920 — validates <see cref="LaborBookletRejectInputDto"/>.</summary>
public sealed class LaborBookletRejectInputDtoValidator
    : AbstractValidator<LaborBookletRejectInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public LaborBookletRejectInputDtoValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(LaborBookletValidatorShared.ReasonMinLength)
            .WithMessage($"Reason must be at least {LaborBookletValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(LaborBookletValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason cannot exceed {LaborBookletValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>R0920 — validates <see cref="ScannedCopyAttachmentInputDto"/>.</summary>
public sealed class ScannedCopyAttachmentInputDtoValidator
    : AbstractValidator<ScannedCopyAttachmentInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public ScannedCopyAttachmentInputDtoValidator()
    {
        RuleFor(x => x.FileBase64)
            .NotEmpty().WithMessage("FileBase64 is required.");

        RuleFor(x => x.FileName)
            .NotEmpty().WithMessage("FileName is required.")
            .MaximumLength(255).WithMessage("FileName cannot exceed 255 characters.");

        RuleFor(x => x.OcrExtractedJson!)
            .MaximumLength(LaborBookletValidatorShared.MaxOcrExtractedJsonLength)
            .When(x => x.OcrExtractedJson is not null)
            .WithMessage($"OcrExtractedJson cannot exceed {LaborBookletValidatorShared.MaxOcrExtractedJsonLength:N0} characters.");

        RuleFor(x => x.OcrConfidenceLevel!)
            .Must(level => LaborBookletValidatorShared.IsKindIn(level, LaborBookletValidatorShared.AllowedConfidenceLevels))
            .When(x => x.OcrConfidenceLevel is not null)
            .WithMessage("OcrConfidenceLevel must be one of High, Medium, Low.");
    }
}

/// <summary>R0921 — validates <see cref="InsuredPersonPre1999PeriodInputDto"/>.</summary>
public sealed class InsuredPersonPre1999PeriodInputDtoValidator
    : AbstractValidator<InsuredPersonPre1999PeriodInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public InsuredPersonPre1999PeriodInputDtoValidator()
    {
        RuleFor(x => x.PeriodStartDate)
            .LessThanOrEqualTo(x => x.PeriodEndDate)
            .WithMessage("PeriodStartDate must be <= PeriodEndDate.");

        RuleFor(x => x.PeriodEndDate)
            .LessThanOrEqualTo(LaborBookletValidatorShared.Pre1999CutOff)
            .WithMessage("PeriodEndDate must be on or before 1998-12-31 (01.01.1999 transition).");

        RuleFor(x => x.DaysWorked!.Value)
            .InclusiveBetween(0, 366)
            .When(x => x.DaysWorked.HasValue)
            .WithMessage("DaysWorked must be in the range 0..366 when supplied.");

        RuleFor(x => x.EmployerName!)
            .MinimumLength(1)
            .MaximumLength(LaborBookletValidatorShared.NameMaxLength)
            .When(x => x.EmployerName is not null)
            .WithMessage($"EmployerName must be 1..{LaborBookletValidatorShared.NameMaxLength} characters when supplied.");

        RuleFor(x => x.Position!)
            .MinimumLength(1)
            .MaximumLength(LaborBookletValidatorShared.NameMaxLength)
            .When(x => x.Position is not null)
            .WithMessage($"Position must be 1..{LaborBookletValidatorShared.NameMaxLength} characters when supplied.");

        RuleFor(x => x.ProofDocumentReference!)
            .MaximumLength(LaborBookletValidatorShared.ProofDocumentReferenceMaxLength)
            .When(x => x.ProofDocumentReference is not null)
            .WithMessage($"ProofDocumentReference cannot exceed {LaborBookletValidatorShared.ProofDocumentReferenceMaxLength} characters.");

        RuleFor(x => x.Notes!)
            .MinimumLength(LaborBookletValidatorShared.ReasonMinLength)
            .MaximumLength(LaborBookletValidatorShared.ReasonMaxLength)
            .When(x => x.Notes is not null)
            .WithMessage($"Notes must be {LaborBookletValidatorShared.ReasonMinLength}..{LaborBookletValidatorShared.ReasonMaxLength} characters when supplied.");

        RuleFor(x => x.ChangeReason!)
            .MinimumLength(LaborBookletValidatorShared.ReasonMinLength)
            .MaximumLength(LaborBookletValidatorShared.ReasonMaxLength)
            .When(x => x.ChangeReason is not null)
            .WithMessage($"ChangeReason must be {LaborBookletValidatorShared.ReasonMinLength}..{LaborBookletValidatorShared.ReasonMaxLength} characters when supplied.");
    }
}
