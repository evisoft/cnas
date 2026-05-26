using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R2270 / TOR SEC 023-024 — shared constants + helpers for the user-group
/// input validators. Centralised so the regex and bounds do not drift across
/// rule sets.
/// </summary>
internal static class UserGroupValidatorShared
{
    /// <summary>Minimum permitted reason / change-reason length.</summary>
    public const int ReasonMinLength = 3;

    /// <summary>Maximum permitted reason / change-reason length.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>Maximum permitted code length (also enforced via regex tail).</summary>
    public const int CodeMaxLength = 64;

    /// <summary>Minimum permitted display-name length.</summary>
    public const int DisplayNameMinLength = 3;

    /// <summary>Maximum permitted display-name length.</summary>
    public const int DisplayNameMaxLength = 256;

    /// <summary>Maximum permitted description length.</summary>
    public const int DescriptionMaxLength = 1000;

    /// <summary>Maximum permitted role-list size.</summary>
    public const int RolesMaxCount = 50;

    /// <summary>Compiled regex for group codes and role codes — UPPER + digits + underscore, 2..64 chars, must start with a letter.</summary>
    public static readonly Regex CodeRegex = new(
        "^[A-Z][A-Z0-9_]{1,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    /// <summary>True when <paramref name="kind"/> parses to a known <see cref="UserGroupKind"/> name (case-sensitive).</summary>
    /// <param name="kind">Candidate enum-name string.</param>
    /// <returns>True when the value parses.</returns>
    public static bool IsValidKind(string? kind) =>
        kind is not null && Enum.TryParse<UserGroupKind>(kind, ignoreCase: false, out _);
}

/// <summary>
/// R2270 — validates <see cref="UserGroupCreateInputDto"/>. Enforces the code /
/// display-name / description / kind / roles bounds.
/// </summary>
public sealed class UserGroupCreateInputValidator : AbstractValidator<UserGroupCreateInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public UserGroupCreateInputValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Code is required.")
            .MaximumLength(UserGroupValidatorShared.CodeMaxLength)
            .WithMessage($"Code cannot exceed {UserGroupValidatorShared.CodeMaxLength} characters.")
            .Must(code => UserGroupValidatorShared.CodeRegex.IsMatch(code ?? string.Empty))
            .WithMessage("Code must match ^[A-Z][A-Z0-9_]{1,63}$ (uppercase letter first, then upper/digits/underscore).");

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage("DisplayName is required.")
            .MinimumLength(UserGroupValidatorShared.DisplayNameMinLength)
            .WithMessage($"DisplayName must be at least {UserGroupValidatorShared.DisplayNameMinLength} characters.")
            .MaximumLength(UserGroupValidatorShared.DisplayNameMaxLength)
            .WithMessage($"DisplayName cannot exceed {UserGroupValidatorShared.DisplayNameMaxLength} characters.");

        RuleFor(x => x.Description!)
            .MaximumLength(UserGroupValidatorShared.DescriptionMaxLength)
            .WithMessage($"Description cannot exceed {UserGroupValidatorShared.DescriptionMaxLength} characters.")
            .When(x => x.Description is not null);

        RuleFor(x => x.Kind)
            .NotEmpty().WithMessage("Kind is required.")
            .Must(UserGroupValidatorShared.IsValidKind)
            .WithMessage("Kind must be one of OrganizationalUnit, FunctionalTeam, Project, Custom.");

        RuleFor(x => x.Roles)
            .NotNull().WithMessage("Roles is required (use an empty list when none).")
            .Must(r => r is null || r.Count <= UserGroupValidatorShared.RolesMaxCount)
            .WithMessage($"Roles cannot exceed {UserGroupValidatorShared.RolesMaxCount} entries.");

        RuleForEach(x => x.Roles)
            .Must(role => UserGroupValidatorShared.CodeRegex.IsMatch(role ?? string.Empty))
            .WithMessage("Each role code must match ^[A-Z][A-Z0-9_]{1,63}$.")
            .When(x => x.Roles is not null);
    }
}

/// <summary>
/// R2270 — validates <see cref="UserGroupModifyInputDto"/>. Each nullable
/// field is validated only when supplied; <c>ChangeReason</c> is always
/// required.
/// </summary>
public sealed class UserGroupModifyInputValidator : AbstractValidator<UserGroupModifyInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public UserGroupModifyInputValidator()
    {
        RuleFor(x => x.DisplayName!)
            .MinimumLength(UserGroupValidatorShared.DisplayNameMinLength)
            .WithMessage($"DisplayName must be at least {UserGroupValidatorShared.DisplayNameMinLength} characters.")
            .MaximumLength(UserGroupValidatorShared.DisplayNameMaxLength)
            .WithMessage($"DisplayName cannot exceed {UserGroupValidatorShared.DisplayNameMaxLength} characters.")
            .When(x => x.DisplayName is not null);

        RuleFor(x => x.Description!)
            .MaximumLength(UserGroupValidatorShared.DescriptionMaxLength)
            .WithMessage($"Description cannot exceed {UserGroupValidatorShared.DescriptionMaxLength} characters.")
            .When(x => x.Description is not null);

        RuleFor(x => x.Kind!)
            .Must(UserGroupValidatorShared.IsValidKind)
            .WithMessage("Kind must be one of OrganizationalUnit, FunctionalTeam, Project, Custom.")
            .When(x => x.Kind is not null);

        RuleFor(x => x.Roles!)
            .Must(r => r.Count <= UserGroupValidatorShared.RolesMaxCount)
            .WithMessage($"Roles cannot exceed {UserGroupValidatorShared.RolesMaxCount} entries.")
            .When(x => x.Roles is not null);

        RuleForEach(x => x.Roles!)
            .Must(role => UserGroupValidatorShared.CodeRegex.IsMatch(role ?? string.Empty))
            .WithMessage("Each role code must match ^[A-Z][A-Z0-9_]{1,63}$.")
            .When(x => x.Roles is not null);

        RuleFor(x => x.ChangeReason)
            .NotEmpty().WithMessage("ChangeReason is required.")
            .MinimumLength(UserGroupValidatorShared.ReasonMinLength)
            .WithMessage($"ChangeReason must be at least {UserGroupValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(UserGroupValidatorShared.ReasonMaxLength)
            .WithMessage($"ChangeReason cannot exceed {UserGroupValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R2270 — validates <see cref="UserGroupReasonInputDto"/> for the
/// disable/enable/delete and nested-membership endpoints. Enforces the
/// standard 3..500 char reason shape.
/// </summary>
public sealed class UserGroupReasonInputValidator : AbstractValidator<UserGroupReasonInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public UserGroupReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(UserGroupValidatorShared.ReasonMinLength)
            .WithMessage($"Reason must be at least {UserGroupValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(UserGroupValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason cannot exceed {UserGroupValidatorShared.ReasonMaxLength} characters.");
    }
}
