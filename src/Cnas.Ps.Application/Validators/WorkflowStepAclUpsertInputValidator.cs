using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0126 / CF 16.10 — validator for <see cref="WorkflowStepAclUpsertInput"/>. Enforces
/// the per-string-list element caps + the explicit permission-code shape required by
/// the ACL service.
/// </summary>
/// <remarks>
/// <para>
/// <b>Permission shape.</b> When <see cref="WorkflowStepAclUpsertInput.RequiredPermission"/>
/// is non-null it must match <see cref="PermissionPattern"/> — uppercase-led, dotted
/// segments, no whitespace. The pattern is intentionally narrow so that a stray empty
/// / lowercase / whitespace value cannot silently pass the ACL check.
/// </para>
/// <para>
/// <b>Element caps.</b> Each entry in <see cref="WorkflowStepAclUpsertInput.RequiredRoles"/>
/// / <see cref="WorkflowStepAclUpsertInput.RequiredGroups"/> is capped at 64 chars,
/// matching the role/group code length convention used elsewhere in the codebase
/// (<see cref="Cnas.Ps.Core.Domain.UserProfile.Roles"/> entries).
/// </para>
/// </remarks>
public sealed class WorkflowStepAclUpsertInputValidator : AbstractValidator<WorkflowStepAclUpsertInput>
{
    /// <summary>
    /// Stable regex for the permission code shape. Uppercase initial char, then
    /// letters / digits / dots only. Anchored.
    /// </summary>
    public const string PermissionPattern = "^[A-Z][A-Za-z0-9.]+$";

    /// <summary>Creates the validator with the static rule set.</summary>
    public WorkflowStepAclUpsertInputValidator()
    {
        RuleFor(x => x.RequiredRoles)
            .NotNull().WithMessage("RequiredRoles is required (may be empty).");
        RuleForEach(x => x.RequiredRoles)
            .NotEmpty().WithMessage("RequiredRoles entries cannot be empty.")
            .MaximumLength(64).WithMessage("RequiredRoles entries cannot exceed 64 characters.");

        RuleFor(x => x.RequiredGroups)
            .NotNull().WithMessage("RequiredGroups is required (may be empty).");
        RuleForEach(x => x.RequiredGroups)
            .NotEmpty().WithMessage("RequiredGroups entries cannot be empty.")
            .MaximumLength(64).WithMessage("RequiredGroups entries cannot exceed 64 characters.");

        RuleFor(x => x.RequiredPermission)
            .Matches(PermissionPattern)
            .When(x => !string.IsNullOrEmpty(x.RequiredPermission))
            .WithMessage("RequiredPermission must match " + PermissionPattern + " when supplied.");

        RuleFor(x => x.Description)
            .MaximumLength(512).WithMessage("Description exceeds the 512-character cap.")
            .When(x => !string.IsNullOrWhiteSpace(x.Description));
    }

    /// <summary>
    /// Test-friendly helper: returns <c>true</c> when the supplied permission string
    /// matches <see cref="PermissionPattern"/> (or is null/whitespace, which the
    /// validator silently allows).
    /// </summary>
    /// <param name="permission">Permission string to check.</param>
    /// <returns><c>true</c> on a valid permission OR null/empty.</returns>
    public static bool PermissionShapeIsValid(string? permission)
    {
        if (string.IsNullOrWhiteSpace(permission)) return true;
        return Regex.IsMatch(permission, PermissionPattern, RegexOptions.CultureInvariant);
    }
}
