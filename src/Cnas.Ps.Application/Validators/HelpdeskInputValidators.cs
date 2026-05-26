using System;
using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R2500 / TOR PIR 020-023 — validates <see cref="SupportTicketCategoryCreateInputDto"/>.
/// Pins the category-code regex, severity enum-membership, SLA-minute bounds, and
/// escalation-queue-code regex.
/// </summary>
public sealed class SupportTicketCategoryCreateInputValidator
    : AbstractValidator<SupportTicketCategoryCreateInputDto>
{
    /// <summary>Stable category-code regex — SCREAMING_SNAKE_CASE, ≤ 64 chars.</summary>
    public const string CategoryCodeRegex = "^[A-Z][A-Z0-9_]{1,63}$";

    /// <summary>Compiled <see cref="CategoryCodeRegex"/> instance.</summary>
    private static readonly Regex CompiledCategoryCode = new(
        CategoryCodeRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Lower bound on FirstResponseSlaMinutes (5 minutes).</summary>
    public const int MinFirstResponseSlaMinutes = 5;

    /// <summary>Upper bound on FirstResponseSlaMinutes (7200 minutes = 5 days).</summary>
    public const int MaxFirstResponseSlaMinutes = 7200;

    /// <summary>Lower bound on ResolutionSlaMinutes (30 minutes).</summary>
    public const int MinResolutionSlaMinutes = 30;

    /// <summary>Upper bound on ResolutionSlaMinutes (43200 minutes = 30 days).</summary>
    public const int MaxResolutionSlaMinutes = 43200;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public SupportTicketCategoryCreateInputValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Code is required.")
            .MaximumLength(64).WithMessage("Code must be 64 characters or fewer.")
            .Must(s => s is not null && CompiledCategoryCode.IsMatch(s))
            .WithMessage("Code must match the SCREAMING_SNAKE_CASE pattern.");

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage("DisplayName is required.")
            .MinimumLength(3).WithMessage("DisplayName must be 3 characters or more.")
            .MaximumLength(256).WithMessage("DisplayName must be 256 characters or fewer.");

        RuleFor(x => x.Description)
            .MaximumLength(1000).WithMessage("Description must be 1000 characters or fewer.");

        RuleFor(x => x.DefaultSeverity)
            .NotEmpty().WithMessage("DefaultSeverity is required.")
            .Must(IsKnownSeverity)
            .WithMessage("DefaultSeverity must be a stable SupportTicketSeverity enum-name.");

        RuleFor(x => x.FirstResponseSlaMinutes)
            .InclusiveBetween(MinFirstResponseSlaMinutes, MaxFirstResponseSlaMinutes)
            .WithMessage($"FirstResponseSlaMinutes must be in [{MinFirstResponseSlaMinutes}, {MaxFirstResponseSlaMinutes}].");

        RuleFor(x => x.ResolutionSlaMinutes)
            .InclusiveBetween(MinResolutionSlaMinutes, MaxResolutionSlaMinutes)
            .WithMessage($"ResolutionSlaMinutes must be in [{MinResolutionSlaMinutes}, {MaxResolutionSlaMinutes}].");

        RuleFor(x => x.EscalationQueueCode)
            .NotEmpty().WithMessage("EscalationQueueCode is required.")
            .MaximumLength(64).WithMessage("EscalationQueueCode must be 64 characters or fewer.")
            .Must(s => s is not null && CompiledCategoryCode.IsMatch(s))
            .WithMessage("EscalationQueueCode must match the SCREAMING_SNAKE_CASE pattern.");
    }

    /// <summary>Returns <c>true</c> when <paramref name="severity"/> parses to a <see cref="SupportTicketSeverity"/>.</summary>
    /// <param name="severity">Candidate severity.</param>
    /// <returns><c>true</c> iff the value is a known enum-name.</returns>
    internal static bool IsKnownSeverity(string? severity)
        => !string.IsNullOrWhiteSpace(severity)
           && Enum.TryParse<SupportTicketSeverity>(severity, ignoreCase: false, out _);
}

/// <summary>R2500 / TOR PIR 020-023 — validates <see cref="SupportTicketCategoryModifyInputDto"/>.</summary>
public sealed class SupportTicketCategoryModifyInputValidator
    : AbstractValidator<SupportTicketCategoryModifyInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public SupportTicketCategoryModifyInputValidator()
    {
        RuleFor(x => x.DisplayName)
            .MinimumLength(3).When(x => x.DisplayName is not null)
            .WithMessage("DisplayName must be 3 characters or more.")
            .MaximumLength(256).When(x => x.DisplayName is not null)
            .WithMessage("DisplayName must be 256 characters or fewer.");

        RuleFor(x => x.Description)
            .MaximumLength(1000).When(x => x.Description is not null)
            .WithMessage("Description must be 1000 characters or fewer.");

        RuleFor(x => x.DefaultSeverity)
            .Must(s => s is null || SupportTicketCategoryCreateInputValidator.IsKnownSeverity(s))
            .WithMessage("DefaultSeverity must be a stable SupportTicketSeverity enum-name.");

        RuleFor(x => x.FirstResponseSlaMinutes)
            .InclusiveBetween(
                SupportTicketCategoryCreateInputValidator.MinFirstResponseSlaMinutes,
                SupportTicketCategoryCreateInputValidator.MaxFirstResponseSlaMinutes)
            .When(x => x.FirstResponseSlaMinutes is not null)
            .WithMessage($"FirstResponseSlaMinutes must be in [{SupportTicketCategoryCreateInputValidator.MinFirstResponseSlaMinutes}, {SupportTicketCategoryCreateInputValidator.MaxFirstResponseSlaMinutes}].");

        RuleFor(x => x.ResolutionSlaMinutes)
            .InclusiveBetween(
                SupportTicketCategoryCreateInputValidator.MinResolutionSlaMinutes,
                SupportTicketCategoryCreateInputValidator.MaxResolutionSlaMinutes)
            .When(x => x.ResolutionSlaMinutes is not null)
            .WithMessage($"ResolutionSlaMinutes must be in [{SupportTicketCategoryCreateInputValidator.MinResolutionSlaMinutes}, {SupportTicketCategoryCreateInputValidator.MaxResolutionSlaMinutes}].");

        RuleFor(x => x.EscalationQueueCode)
            .MaximumLength(64).When(x => x.EscalationQueueCode is not null)
            .WithMessage("EscalationQueueCode must be 64 characters or fewer.");

        RuleFor(x => x.ChangeReason)
            .NotEmpty().WithMessage("ChangeReason is required.")
            .MinimumLength(3).WithMessage("ChangeReason must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("ChangeReason must be 1000 characters or fewer.");
    }
}

/// <summary>R2500 / TOR PIR 020-023 — validates <see cref="SupportTicketCategoryReasonInputDto"/>.</summary>
public sealed class SupportTicketCategoryReasonInputValidator
    : AbstractValidator<SupportTicketCategoryReasonInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public SupportTicketCategoryReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("Reason must be 1000 characters or fewer.");
    }
}

/// <summary>R2500 / TOR PIR 020-023 — validates <see cref="SupportTicketCategoryFilterDto"/>.</summary>
public sealed class SupportTicketCategoryFilterValidator
    : AbstractValidator<SupportTicketCategoryFilterDto>
{
    /// <summary>Upper bound on Take.</summary>
    public const int MaxTake = 100;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public SupportTicketCategoryFilterValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0).WithMessage("Skip must be 0 or greater.");
        RuleFor(x => x.Take).InclusiveBetween(1, MaxTake).WithMessage($"Take must be in [1, {MaxTake}].");
    }
}

/// <summary>R2500 / TOR PIR 020-023 — validates <see cref="SupportTicketSubmitInputDto"/>.</summary>
public sealed class SupportTicketSubmitInputValidator : AbstractValidator<SupportTicketSubmitInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public SupportTicketSubmitInputValidator()
    {
        RuleFor(x => x.CategoryCode)
            .NotEmpty().WithMessage("CategoryCode is required.")
            .MaximumLength(64).WithMessage("CategoryCode must be 64 characters or fewer.");

        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MinimumLength(3).WithMessage("Title must be 3 characters or more.")
            .MaximumLength(256).WithMessage("Title must be 256 characters or fewer.");

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Description is required.")
            .MinimumLength(3).WithMessage("Description must be 3 characters or more.")
            .MaximumLength(8000).WithMessage("Description must be 8000 characters or fewer.");

        RuleFor(x => x.Severity)
            .Must(s => s is null || SupportTicketCategoryCreateInputValidator.IsKnownSeverity(s))
            .WithMessage("Severity must be a stable SupportTicketSeverity enum-name when supplied.");
    }
}

/// <summary>R2500 / TOR PIR 020-023 — validates <see cref="SupportTicketAssignInputDto"/>.</summary>
public sealed class SupportTicketAssignInputValidator : AbstractValidator<SupportTicketAssignInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public SupportTicketAssignInputValidator()
    {
        RuleFor(x => x.AssignedToUserSqid)
            .NotEmpty().WithMessage("AssignedToUserSqid is required.")
            .MaximumLength(64).WithMessage("AssignedToUserSqid must be 64 characters or fewer.");

        RuleFor(x => x.Note)
            .NotEmpty().WithMessage("Note is required.")
            .MinimumLength(3).WithMessage("Note must be 3 characters or more.")
            .MaximumLength(500).WithMessage("Note must be 500 characters or fewer.");
    }
}

/// <summary>R2500 / TOR PIR 020-023 — validates <see cref="SupportTicketResolutionInputDto"/>.</summary>
public sealed class SupportTicketResolutionInputValidator
    : AbstractValidator<SupportTicketResolutionInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public SupportTicketResolutionInputValidator()
    {
        RuleFor(x => x.Summary)
            .NotEmpty().WithMessage("Summary is required.")
            .MinimumLength(3).WithMessage("Summary must be 3 characters or more.")
            .MaximumLength(2000).WithMessage("Summary must be 2000 characters or fewer.");
    }
}

/// <summary>R2500 / TOR PIR 020-023 — validates <see cref="SupportTicketReasonInputDto"/>.</summary>
public sealed class SupportTicketReasonInputValidator : AbstractValidator<SupportTicketReasonInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public SupportTicketReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be 3 characters or more.")
            .MaximumLength(500).WithMessage("Reason must be 500 characters or fewer.");
    }
}

/// <summary>R2500 / TOR PIR 020-023 — validates <see cref="SupportTicketCommentInputDto"/>.</summary>
public sealed class SupportTicketCommentInputValidator : AbstractValidator<SupportTicketCommentInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public SupportTicketCommentInputValidator()
    {
        RuleFor(x => x.Body)
            .NotEmpty().WithMessage("Body is required.")
            .MinimumLength(3).WithMessage("Body must be 3 characters or more.")
            .MaximumLength(8000).WithMessage("Body must be 8000 characters or fewer.");
    }
}

/// <summary>R2500 / TOR PIR 020-023 — validates <see cref="SupportTicketFilterDto"/>.</summary>
public sealed class SupportTicketFilterValidator : AbstractValidator<SupportTicketFilterDto>
{
    /// <summary>Upper bound on Take.</summary>
    public const int MaxTake = 100;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public SupportTicketFilterValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0).WithMessage("Skip must be 0 or greater.");
        RuleFor(x => x.Take).InclusiveBetween(1, MaxTake).WithMessage($"Take must be in [1, {MaxTake}].");

        RuleFor(x => x.Status)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<SupportTicketStatus>(s, ignoreCase: false, out _))
            .WithMessage("Status must be a stable SupportTicketStatus enum-name when supplied.");

        RuleFor(x => x.Severity)
            .Must(s => string.IsNullOrEmpty(s)
                || SupportTicketCategoryCreateInputValidator.IsKnownSeverity(s))
            .WithMessage("Severity must be a stable SupportTicketSeverity enum-name when supplied.");
    }
}
