using System;
using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0196 / TOR CF 23.02 — validates <see cref="AuditCategoryCreateInputDto"/>.
/// Pins the category-code regex and severity enum-membership.
/// </summary>
public sealed class AuditCategoryCreateInputValidator
    : AbstractValidator<AuditCategoryCreateInputDto>
{
    /// <summary>
    /// Stable category-code regex — SCREAMING_SNAKE_CASE with optional dotted
    /// namespace segments (matches the TOR CF 23.02 seed codes such as
    /// <c>APPLICATION.RECEIVE</c>), ≤ 64 chars total.
    /// </summary>
    public const string CategoryCodeRegex = "^[A-Z][A-Z0-9_.]{1,63}$";

    /// <summary>Compiled <see cref="CategoryCodeRegex"/> instance.</summary>
    private static readonly Regex CompiledCategoryCode = new(
        CategoryCodeRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public AuditCategoryCreateInputValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Code is required.")
            .MaximumLength(64).WithMessage("Code must be 64 characters or fewer.")
            .Must(s => s is not null && CompiledCategoryCode.IsMatch(s))
            .WithMessage("Code must match the SCREAMING_SNAKE_CASE pattern (dots allowed for namespaces).");

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage("DisplayName is required.")
            .MinimumLength(3).WithMessage("DisplayName must be 3 characters or more.")
            .MaximumLength(256).WithMessage("DisplayName must be 256 characters or fewer.");

        RuleFor(x => x.Description)
            .MaximumLength(1000).WithMessage("Description must be 1000 characters or fewer.");

        RuleFor(x => x.DefaultSeverity)
            .NotEmpty().WithMessage("DefaultSeverity is required.")
            .Must(IsKnownSeverity)
            .WithMessage("DefaultSeverity must be a stable AuditSeverity enum-name.");
    }

    /// <summary>Returns <c>true</c> when <paramref name="severity"/> parses to a known <see cref="AuditSeverity"/>.</summary>
    /// <param name="severity">Candidate severity.</param>
    /// <returns><c>true</c> iff the value is a known enum-name.</returns>
    internal static bool IsKnownSeverity(string? severity)
        => !string.IsNullOrWhiteSpace(severity)
           && Enum.TryParse<AuditSeverity>(severity, ignoreCase: false, out _);
}

/// <summary>R0196 / TOR CF 23.02 — validates <see cref="AuditCategoryModifyInputDto"/>.</summary>
public sealed class AuditCategoryModifyInputValidator
    : AbstractValidator<AuditCategoryModifyInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public AuditCategoryModifyInputValidator()
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
            .Must(s => s is null || AuditCategoryCreateInputValidator.IsKnownSeverity(s))
            .WithMessage("DefaultSeverity must be a stable AuditSeverity enum-name.");

        RuleFor(x => x.ChangeReason)
            .NotEmpty().WithMessage("ChangeReason is required.")
            .MinimumLength(3).WithMessage("ChangeReason must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("ChangeReason must be 1000 characters or fewer.");
    }
}

/// <summary>R0196 / TOR CF 23.02 — validates <see cref="AuditCategoryReasonInputDto"/>.</summary>
public sealed class AuditCategoryReasonInputValidator
    : AbstractValidator<AuditCategoryReasonInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public AuditCategoryReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("Reason must be 1000 characters or fewer.");
    }
}

/// <summary>R0196 / TOR CF 23.02 — validates <see cref="AuditCategoryFilterDto"/>.</summary>
public sealed class AuditCategoryFilterValidator
    : AbstractValidator<AuditCategoryFilterDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public AuditCategoryFilterValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0).WithMessage("Skip must be ≥ 0.");

        RuleFor(x => x.Take)
            .InclusiveBetween(1, 100).WithMessage("Take must be in [1, 100].");
    }
}
