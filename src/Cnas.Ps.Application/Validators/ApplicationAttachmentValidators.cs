using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0322 / TOR UI 014 — FluentValidation rules for the
/// <see cref="ApplicationAttachInputDto"/> attach payload. Enforces category
/// allow-list, document-sqid presence, and notes length cap.
/// </summary>
public sealed class ApplicationAttachInputDtoValidator : AbstractValidator<ApplicationAttachInputDto>
{
    /// <summary>Maximum length of the optional Notes annotation (chars).</summary>
    public const int NotesMaxLength = 500;

    /// <summary>Wires the rules at construction time.</summary>
    public ApplicationAttachInputDtoValidator()
    {
        RuleFor(x => x.DocumentSqid)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("DocumentSqid is required.");

        RuleFor(x => x.Category)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("Category is required.")
            .Must(IsValidCategory)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("Category must be one of: Identity, Income, MedicalReport, Birth, Death, Marriage, Custody, Other.");

        RuleFor(x => x.Notes)
            .MaximumLength(NotesMaxLength)
            .When(x => x.Notes is not null)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage($"Notes must be ≤ {NotesMaxLength} characters when supplied.");
    }

    /// <summary>Returns true when the supplied label parses to <see cref="ApplicationAttachmentCategory"/>.</summary>
    /// <param name="raw">Candidate enum-name string.</param>
    /// <returns>Whether the name resolves to a defined enum value.</returns>
    private static bool IsValidCategory(string? raw)
        => !string.IsNullOrWhiteSpace(raw)
        && Enum.TryParse<ApplicationAttachmentCategory>(raw, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed);
}

/// <summary>
/// R0322 — FluentValidation rules for the reason payload accepted by the
/// remove-attachment endpoint. Reason is required, 3..500 chars.
/// </summary>
public sealed class ApplicationAttachmentReasonInputDtoValidator : AbstractValidator<ApplicationAttachmentReasonInputDto>
{
    /// <summary>Minimum length of the operator-supplied removal reason.</summary>
    public const int ReasonMinLength = 3;

    /// <summary>Maximum length of the operator-supplied removal reason.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>Wires the rules at construction time.</summary>
    public ApplicationAttachmentReasonInputDtoValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("Reason is required.")
            .Length(ReasonMinLength, ReasonMaxLength)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage($"Reason must be {ReasonMinLength}..{ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R0322 — FluentValidation rules for the virus-scan result payload. The status
/// must resolve to a TERMINAL <see cref="AttachmentVirusScanStatus"/> value
/// (<c>Pending</c> is rejected — that's the birth state, not a scan result).
/// </summary>
public sealed class ApplicationAttachmentScanResultInputDtoValidator
    : AbstractValidator<ApplicationAttachmentScanResultInputDto>
{
    /// <summary>Maximum length of the scanner-name tag.</summary>
    public const int ScannerNameMaxLength = 64;

    /// <summary>Maximum length of the optional Notes annotation.</summary>
    public const int NotesMaxLength = 500;

    /// <summary>Wires the rules at construction time.</summary>
    public ApplicationAttachmentScanResultInputDtoValidator()
    {
        RuleFor(x => x.Status)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("Status is required.")
            .Must(IsTerminalStatus)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("Status must be one of: Clean, Infected, ScanFailed, Skipped.");

        RuleFor(x => x.ScannerName)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("ScannerName is required.")
            .MaximumLength(ScannerNameMaxLength)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage($"ScannerName must be ≤ {ScannerNameMaxLength} characters.");

        RuleFor(x => x.Notes)
            .MaximumLength(NotesMaxLength)
            .When(x => x.Notes is not null)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage($"Notes must be ≤ {NotesMaxLength} characters when supplied.");
    }

    /// <summary>Returns true when the supplied label parses to a non-Pending terminal status.</summary>
    /// <param name="raw">Candidate enum-name string.</param>
    /// <returns>Whether the name resolves to a defined terminal enum value.</returns>
    private static bool IsTerminalStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        if (!Enum.TryParse<AttachmentVirusScanStatus>(raw, ignoreCase: true, out var parsed))
        {
            return false;
        }
        return parsed != AttachmentVirusScanStatus.Pending && Enum.IsDefined(parsed);
    }
}

/// <summary>
/// R0322 — FluentValidation rules for the attachment-list query filter. Validates
/// Skip / Take bounds + optional category / status enum names.
/// </summary>
public sealed class ApplicationAttachmentFilterDtoValidator : AbstractValidator<ApplicationAttachmentFilterDto>
{
    /// <summary>Maximum page size accepted at the boundary.</summary>
    public const int MaxTake = 200;

    /// <summary>Wires the rules at construction time.</summary>
    public ApplicationAttachmentFilterDtoValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("Skip must be >= 0.");

        RuleFor(x => x.Take)
            .InclusiveBetween(1, MaxTake)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage($"Take must be in [1, {MaxTake}].");

        RuleFor(x => x.Category)
            .Must(IsValidCategory)
            .When(x => !string.IsNullOrWhiteSpace(x.Category))
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("Category must be one of: Identity, Income, MedicalReport, Birth, Death, Marriage, Custody, Other.");

        RuleFor(x => x.VirusScanStatus)
            .Must(IsValidStatus)
            .When(x => !string.IsNullOrWhiteSpace(x.VirusScanStatus))
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("VirusScanStatus must be one of: Pending, Clean, Infected, ScanFailed, Skipped.");
    }

    /// <summary>Returns true when the value parses to a defined category.</summary>
    /// <param name="raw">Candidate enum-name string.</param>
    /// <returns>Whether the name resolves to a defined enum value.</returns>
    private static bool IsValidCategory(string? raw)
        => string.IsNullOrWhiteSpace(raw)
        || (Enum.TryParse<ApplicationAttachmentCategory>(raw, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed));

    /// <summary>Returns true when the value parses to a defined status.</summary>
    /// <param name="raw">Candidate enum-name string.</param>
    /// <returns>Whether the name resolves to a defined enum value.</returns>
    private static bool IsValidStatus(string? raw)
        => string.IsNullOrWhiteSpace(raw)
        || (Enum.TryParse<AttachmentVirusScanStatus>(raw, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed));
}
