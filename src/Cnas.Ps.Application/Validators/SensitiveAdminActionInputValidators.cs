using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R2273 / TOR SEC 027 — shared helpers + constants for the sensitive-admin-action
/// validators. Centralised so the regex / size bounds don't drift across rule sets.
/// </summary>
internal static partial class SensitiveAdminActionValidatorShared
{
    /// <summary>Minimum reason / note length across reasons + notes.</summary>
    public const int ReasonMinLength = 3;

    /// <summary>Maximum reason / note length across reasons + notes.</summary>
    public const int ReasonMaxLength = 1000;

    /// <summary>Maximum permitted serialised payload size in bytes.</summary>
    public const int PayloadMaxBytes = 8192;

    /// <summary>Maximum permitted page size on the list endpoint.</summary>
    public const int MaxTake = 100;

    /// <summary>
    /// Action-code shape — uppercase ASCII letters / digits / underscore / dot, starting
    /// with a letter, 2..64 chars (entity column cap is 64).
    /// </summary>
    [GeneratedRegex(@"^[A-Z][A-Z0-9_.]{1,63}$", RegexOptions.CultureInvariant)]
    public static partial Regex ActionCodeRegex();

    /// <summary>True when <paramref name="actionCode"/> matches the canonical shape.</summary>
    /// <param name="actionCode">Candidate action code.</param>
    /// <returns><c>true</c> when shape-conformant; <c>false</c> when null/empty or malformed.</returns>
    public static bool ActionCodeIsValid(string? actionCode)
        => !string.IsNullOrWhiteSpace(actionCode) && ActionCodeRegex().IsMatch(actionCode);

    /// <summary>True when the optional action code is null OR matches the canonical shape.</summary>
    /// <param name="actionCode">Candidate action code.</param>
    /// <returns><c>true</c> when null or shape-conformant.</returns>
    public static bool ActionCodeIsValidOrNull(string? actionCode)
        => actionCode is null || ActionCodeRegex().IsMatch(actionCode);

    /// <summary>True when <paramref name="status"/> is null OR parses to a valid <see cref="SensitiveAdminActionStatus"/>.</summary>
    /// <param name="status">Candidate status name.</param>
    /// <returns><c>true</c> when null or a valid enum name.</returns>
    public static bool StatusIsValidOrNull(string? status)
        => status is null
            || Enum.TryParse<SensitiveAdminActionStatus>(status, ignoreCase: false, out _);

    /// <summary>
    /// True when <paramref name="payload"/> is a well-formed JSON document AND the UTF-8
    /// byte count fits within <see cref="PayloadMaxBytes"/>.
    /// </summary>
    /// <param name="payload">Candidate JSON payload.</param>
    /// <returns><c>true</c> when valid + within size; <c>false</c> otherwise.</returns>
    public static bool PayloadIsValid(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }
        if (Encoding.UTF8.GetByteCount(payload) > PayloadMaxBytes)
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// R2273 / TOR SEC 027 — validates <see cref="SensitiveAdminActionRequestInputDto"/>.
/// Enforces the action-code regex, the reason length window, and the JSON payload
/// validity + size cap.
/// </summary>
public sealed class SensitiveAdminActionRequestInputValidator
    : AbstractValidator<SensitiveAdminActionRequestInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public SensitiveAdminActionRequestInputValidator()
    {
        RuleFor(x => x.ActionCode)
            .Must(SensitiveAdminActionValidatorShared.ActionCodeIsValid)
            .WithMessage("ActionCode must be SCREAMING_SNAKE_CASE matching ^[A-Z][A-Z0-9_.]{1,63}$.");

        RuleFor(x => x.RequestReason)
            .NotEmpty().WithMessage("RequestReason is required.")
            .MinimumLength(SensitiveAdminActionValidatorShared.ReasonMinLength)
            .WithMessage($"RequestReason must be at least {SensitiveAdminActionValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(SensitiveAdminActionValidatorShared.ReasonMaxLength)
            .WithMessage($"RequestReason cannot exceed {SensitiveAdminActionValidatorShared.ReasonMaxLength} characters.");

        RuleFor(x => x.RequestPayloadJson)
            .Must(SensitiveAdminActionValidatorShared.PayloadIsValid)
            .WithMessage($"RequestPayloadJson must be well-formed JSON and no larger than {SensitiveAdminActionValidatorShared.PayloadMaxBytes} bytes.");
    }
}

/// <summary>
/// R2273 / TOR SEC 027 — validates <see cref="SensitiveAdminActionApprovalInputDto"/>.
/// The note must be present and 3..1000 chars.
/// </summary>
public sealed class SensitiveAdminActionApprovalInputValidator
    : AbstractValidator<SensitiveAdminActionApprovalInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public SensitiveAdminActionApprovalInputValidator()
    {
        RuleFor(x => x.Note)
            .NotEmpty().WithMessage("Note is required.")
            .MinimumLength(SensitiveAdminActionValidatorShared.ReasonMinLength)
            .WithMessage($"Note must be at least {SensitiveAdminActionValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(SensitiveAdminActionValidatorShared.ReasonMaxLength)
            .WithMessage($"Note cannot exceed {SensitiveAdminActionValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R2273 / TOR SEC 027 — validates <see cref="SensitiveAdminActionReasonInputDto"/>.
/// The reason must be present and 3..1000 chars.
/// </summary>
public sealed class SensitiveAdminActionReasonInputValidator
    : AbstractValidator<SensitiveAdminActionReasonInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public SensitiveAdminActionReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(SensitiveAdminActionValidatorShared.ReasonMinLength)
            .WithMessage($"Reason must be at least {SensitiveAdminActionValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(SensitiveAdminActionValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason cannot exceed {SensitiveAdminActionValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R2273 / TOR SEC 027 — validates <see cref="SensitiveAdminActionFilterDto"/>.
/// Enforces the status-name parse, the action-code regex, and the Skip/Take page bounds.
/// </summary>
public sealed class SensitiveAdminActionFilterValidator
    : AbstractValidator<SensitiveAdminActionFilterDto>
{
    /// <summary>Builds the rule set.</summary>
    public SensitiveAdminActionFilterValidator()
    {
        RuleFor(x => x.Status)
            .Must(SensitiveAdminActionValidatorShared.StatusIsValidOrNull)
            .WithMessage("Status must be one of the SensitiveAdminActionStatus enum names, or null to match any.");

        RuleFor(x => x.ActionCode)
            .Must(SensitiveAdminActionValidatorShared.ActionCodeIsValidOrNull)
            .WithMessage("ActionCode must be SCREAMING_SNAKE_CASE matching ^[A-Z][A-Z0-9_.]{1,63}$ when supplied.");

        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Skip must be >= 0.");

        RuleFor(x => x.Take)
            .GreaterThanOrEqualTo(1)
            .LessThanOrEqualTo(SensitiveAdminActionValidatorShared.MaxTake)
            .WithMessage($"Take must be in 1..{SensitiveAdminActionValidatorShared.MaxTake}.");
    }
}
