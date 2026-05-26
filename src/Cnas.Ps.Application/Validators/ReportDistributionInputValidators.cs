using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R1906 / TOR Annex 6 — shared regex / constants for the report-distribution
/// validators. Centralised so the report-code, email, and recipient-code
/// caps stay aligned across the rule set.
/// </summary>
internal static partial class ReportDistributionValidatorShared
{
    /// <summary>Maximum permitted report-code length (matches the entity column cap).</summary>
    public const int ReportCodeMaxLength = 64;

    /// <summary>Maximum permitted recipient-code length (matches the entity column cap).</summary>
    public const int RecipientCodeMaxLength = 256;

    /// <summary>Maximum permitted notes length.</summary>
    public const int NotesMaxLength = 1000;

    /// <summary>Minimum reason-text length (audit-friendly).</summary>
    public const int ReasonMinLength = 3;

    /// <summary>Maximum reason-text length.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>Maximum permitted Take on a list endpoint.</summary>
    public const int MaxTake = 200;

    /// <summary>Report-code shape — SCREAMING_SNAKE_CASE with optional dot separators.</summary>
    [GeneratedRegex(@"^[A-Z][A-Z0-9_.]{1,63}$", RegexOptions.CultureInvariant)]
    public static partial Regex ReportCodeRegex();

    /// <summary>
    /// Email shape per the iteration spec. Intentionally permissive — the
    /// transport-layer parser performs the canonical validation.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$", RegexOptions.CultureInvariant)]
    public static partial Regex EmailRegex();

    /// <summary>Returns <c>true</c> when the candidate value parses to a valid enum name.</summary>
    /// <typeparam name="TEnum">Enum type to parse against.</typeparam>
    /// <param name="candidate">Candidate enum-name value (case-sensitive).</param>
    /// <returns><c>true</c> when the value matches an enum member.</returns>
    public static bool IsValidEnumName<TEnum>(string? candidate)
        where TEnum : struct, Enum
        => candidate is not null && Enum.TryParse<TEnum>(candidate, ignoreCase: false, out _);

    /// <summary>Returns <c>true</c> when null OR a valid enum name.</summary>
    /// <typeparam name="TEnum">Enum type to parse against.</typeparam>
    /// <param name="candidate">Candidate value.</param>
    /// <returns><c>true</c> when null or parses successfully.</returns>
    public static bool IsValidEnumNameOrNull<TEnum>(string? candidate)
        where TEnum : struct, Enum
        => candidate is null || Enum.TryParse<TEnum>(candidate, ignoreCase: false, out _);

    /// <summary>Returns <c>true</c> when the report-code value matches the canonical shape.</summary>
    /// <param name="candidate">Candidate report code.</param>
    /// <returns><c>true</c> when shape-conformant.</returns>
    public static bool IsValidReportCode(string? candidate)
        => candidate is not null && ReportCodeRegex().IsMatch(candidate);

    /// <summary>Returns <c>true</c> when null OR matches the canonical report-code shape.</summary>
    /// <param name="candidate">Candidate report code.</param>
    /// <returns><c>true</c> when null or shape-conformant.</returns>
    public static bool IsValidReportCodeOrNull(string? candidate)
        => candidate is null || ReportCodeRegex().IsMatch(candidate);
}

/// <summary>
/// R1906 / TOR Annex 6 — validates the create-rule payload. Enforces the
/// report-code shape, the recipient-code shape (with the per-kind email
/// regex), the date-range coherence, and the column-cap on Notes.
/// </summary>
public sealed class ReportDistributionRuleCreateInputValidator
    : AbstractValidator<ReportDistributionRuleCreateInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public ReportDistributionRuleCreateInputValidator()
    {
        RuleFor(x => x.ReportCode)
            .NotEmpty().WithMessage("ReportCode is required.")
            .Must(ReportDistributionValidatorShared.IsValidReportCode)
            .WithMessage("ReportCode must be SCREAMING_SNAKE_CASE matching ^[A-Z][A-Z0-9_.]{1,63}$.");

        RuleFor(x => x.Channel)
            .Must(ReportDistributionValidatorShared.IsValidEnumName<ReportDistributionChannel>)
            .WithMessage("Channel must be one of: InSystem, Dashboard, Email, MNotify.");

        RuleFor(x => x.RecipientKind)
            .Must(ReportDistributionValidatorShared.IsValidEnumName<ReportRecipientKind>)
            .WithMessage("RecipientKind must be one of: User, Group, Role, EmailAddress, MNotifyCategory.");

        RuleFor(x => x.RecipientCode)
            .NotEmpty().WithMessage("RecipientCode is required.")
            .MaximumLength(ReportDistributionValidatorShared.RecipientCodeMaxLength)
            .WithMessage($"RecipientCode cannot exceed {ReportDistributionValidatorShared.RecipientCodeMaxLength} characters.");

        // Email-shape gate is conditional on RecipientKind=EmailAddress.
        RuleFor(x => x.RecipientCode)
            .Must(value => value is not null
                && ReportDistributionValidatorShared.EmailRegex().IsMatch(value))
            .When(x => string.Equals(x.RecipientKind, nameof(ReportRecipientKind.EmailAddress), StringComparison.Ordinal))
            .WithMessage("RecipientCode must be a valid email address when RecipientKind is EmailAddress.");

        RuleFor(x => x.Format)
            .Must(ReportDistributionValidatorShared.IsValidEnumName<ReportDeliveryFormat>)
            .WithMessage("Format must be one of: Pdf, Csv, Xlsx, LinkOnly.");

        RuleFor(x => x.Priority)
            .Must(ReportDistributionValidatorShared.IsValidEnumName<ReportDeliveryPriority>)
            .WithMessage("Priority must be one of: Normal, High, Critical.");

        RuleFor(x => x.EffectiveUntil)
            .Must((dto, until) => until is null || until.Value >= dto.EffectiveFrom)
            .WithMessage("EffectiveUntil must be on or after EffectiveFrom.");

        RuleFor(x => x.Notes!)
            .MaximumLength(ReportDistributionValidatorShared.NotesMaxLength)
            .When(x => x.Notes is not null)
            .WithMessage($"Notes cannot exceed {ReportDistributionValidatorShared.NotesMaxLength} characters.");
    }
}

/// <summary>
/// R1906 / TOR Annex 6 — validates the modify-rule payload. Each field is
/// validated only when supplied (non-null); the mandatory
/// <c>ChangeReason</c> follows the 3..500 char audit-friendly window.
/// </summary>
public sealed class ReportDistributionRuleModifyInputValidator
    : AbstractValidator<ReportDistributionRuleModifyInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public ReportDistributionRuleModifyInputValidator()
    {
        RuleFor(x => x.Channel!)
            .Must(ReportDistributionValidatorShared.IsValidEnumNameOrNull<ReportDistributionChannel>)
            .When(x => x.Channel is not null)
            .WithMessage("Channel must be one of: InSystem, Dashboard, Email, MNotify.");

        RuleFor(x => x.RecipientKind!)
            .Must(ReportDistributionValidatorShared.IsValidEnumNameOrNull<ReportRecipientKind>)
            .When(x => x.RecipientKind is not null)
            .WithMessage("RecipientKind must be one of: User, Group, Role, EmailAddress, MNotifyCategory.");

        RuleFor(x => x.RecipientCode!)
            .NotEmpty()
            .MaximumLength(ReportDistributionValidatorShared.RecipientCodeMaxLength)
            .When(x => x.RecipientCode is not null)
            .WithMessage($"RecipientCode must be 1..{ReportDistributionValidatorShared.RecipientCodeMaxLength} characters when supplied.");

        RuleFor(x => x.RecipientCode!)
            .Must(value => value is not null
                && ReportDistributionValidatorShared.EmailRegex().IsMatch(value))
            .When(x => x.RecipientCode is not null
                && string.Equals(x.RecipientKind, nameof(ReportRecipientKind.EmailAddress), StringComparison.Ordinal))
            .WithMessage("RecipientCode must be a valid email address when RecipientKind is EmailAddress.");

        RuleFor(x => x.Format!)
            .Must(ReportDistributionValidatorShared.IsValidEnumNameOrNull<ReportDeliveryFormat>)
            .When(x => x.Format is not null)
            .WithMessage("Format must be one of: Pdf, Csv, Xlsx, LinkOnly.");

        RuleFor(x => x.Priority!)
            .Must(ReportDistributionValidatorShared.IsValidEnumNameOrNull<ReportDeliveryPriority>)
            .When(x => x.Priority is not null)
            .WithMessage("Priority must be one of: Normal, High, Critical.");

        RuleFor(x => x.EffectiveUntil)
            .Must((dto, until) => until is null || dto.EffectiveFrom is null || until.Value >= dto.EffectiveFrom.Value)
            .When(x => x.EffectiveUntil is not null && x.EffectiveFrom is not null)
            .WithMessage("EffectiveUntil must be on or after EffectiveFrom.");

        RuleFor(x => x.Notes!)
            .MaximumLength(ReportDistributionValidatorShared.NotesMaxLength)
            .When(x => x.Notes is not null)
            .WithMessage($"Notes cannot exceed {ReportDistributionValidatorShared.NotesMaxLength} characters.");

        RuleFor(x => x.ChangeReason)
            .NotEmpty()
            .MinimumLength(ReportDistributionValidatorShared.ReasonMinLength)
            .MaximumLength(ReportDistributionValidatorShared.ReasonMaxLength)
            .WithMessage($"ChangeReason must be {ReportDistributionValidatorShared.ReasonMinLength}..{ReportDistributionValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R1906 / TOR Annex 6 — validates the simple reason payload used by the
/// disable / enable / delete endpoints.
/// </summary>
public sealed class ReportDistributionReasonInputValidator
    : AbstractValidator<ReportDistributionReasonInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public ReportDistributionReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty()
            .MinimumLength(ReportDistributionValidatorShared.ReasonMinLength)
            .MaximumLength(ReportDistributionValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason must be {ReportDistributionValidatorShared.ReasonMinLength}..{ReportDistributionValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R1906 / TOR Annex 6 — validates the rule-list filter envelope.
/// </summary>
public sealed class ReportDistributionRuleFilterValidator
    : AbstractValidator<ReportDistributionRuleFilterDto>
{
    /// <summary>Builds the rule set.</summary>
    public ReportDistributionRuleFilterValidator()
    {
        RuleFor(x => x.ReportCode)
            .Must(ReportDistributionValidatorShared.IsValidReportCodeOrNull)
            .WithMessage("ReportCode must be SCREAMING_SNAKE_CASE matching ^[A-Z][A-Z0-9_.]{1,63}$.");

        RuleFor(x => x.Channel)
            .Must(ReportDistributionValidatorShared.IsValidEnumNameOrNull<ReportDistributionChannel>)
            .WithMessage("Channel must be one of: InSystem, Dashboard, Email, MNotify.");

        RuleFor(x => x.RecipientKind)
            .Must(ReportDistributionValidatorShared.IsValidEnumNameOrNull<ReportRecipientKind>)
            .WithMessage("RecipientKind must be one of: User, Group, Role, EmailAddress, MNotifyCategory.");

        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0).WithMessage("Skip must be >= 0.");
        RuleFor(x => x.Take)
            .GreaterThanOrEqualTo(1)
            .LessThanOrEqualTo(ReportDistributionValidatorShared.MaxTake)
            .WithMessage($"Take must be in 1..{ReportDistributionValidatorShared.MaxTake}.");
    }
}

/// <summary>
/// R1906 / TOR Annex 6 — validates the dispatch-list filter envelope.
/// </summary>
public sealed class ReportDispatchFilterValidator
    : AbstractValidator<ReportDispatchFilterDto>
{
    /// <summary>Builds the rule set.</summary>
    public ReportDispatchFilterValidator()
    {
        RuleFor(x => x.Status)
            .Must(ReportDistributionValidatorShared.IsValidEnumNameOrNull<ReportDispatchStatus>)
            .WithMessage("Status must be one of: Pending, Delivered, Failed, Skipped.");

        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0).WithMessage("Skip must be >= 0.");
        RuleFor(x => x.Take)
            .GreaterThanOrEqualTo(1)
            .LessThanOrEqualTo(ReportDistributionValidatorShared.MaxTake)
            .WithMessage($"Take must be in 1..{ReportDistributionValidatorShared.MaxTake}.");
    }
}

/// <summary>
/// R1906 / TOR Annex 6 — validates the dispatch-trigger envelope.
/// </summary>
public sealed class ReportDispatchInputValidator
    : AbstractValidator<ReportDispatchInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public ReportDispatchInputValidator()
    {
        RuleFor(x => x.ReportCode)
            .NotEmpty()
            .Must(ReportDistributionValidatorShared.IsValidReportCode)
            .WithMessage("ReportCode must be SCREAMING_SNAKE_CASE matching ^[A-Z][A-Z0-9_.]{1,63}$.");

        RuleFor(x => x.ReportRunSqid)
            .NotEmpty().WithMessage("ReportRunSqid is required.")
            .MaximumLength(64)
            .WithMessage("ReportRunSqid cannot exceed 64 characters.");

        RuleFor(x => x.Format)
            .Must(ReportDistributionValidatorShared.IsValidEnumName<ReportDeliveryFormat>)
            .WithMessage("Format must be one of: Pdf, Csv, Xlsx, LinkOnly.");

        RuleFor(x => x.ReportTitle)
            .NotEmpty()
            .MaximumLength(256)
            .WithMessage("ReportTitle must be 1..256 characters.");

        RuleFor(x => x.ReportSummary)
            .MaximumLength(1000)
            .WithMessage("ReportSummary cannot exceed 1000 characters.");

        RuleFor(x => x.PayloadDownloadUrl)
            .NotEmpty()
            .MaximumLength(500)
            .WithMessage("PayloadDownloadUrl must be 1..500 characters.");
    }
}
