using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0183 / SEC 043 — validator for <see cref="AuditFieldPolicyCreateInput"/>.
/// Enforces the PascalCase EntityType shape (matches the CLR type's <c>Name</c>
/// invariant), the severity-string parse, and the
/// "RequireAnyChange ⇒ TrackedFields non-empty" combination safeguard.
/// </summary>
/// <remarks>
/// Pairs with <see cref="AuditFieldPolicyUpdateInputValidator"/> — the two
/// validators duplicate the field-level rules they share. Inline duplication keeps
/// each validator self-contained; the shared bits are small.
/// </remarks>
public sealed class AuditFieldPolicyInputValidator : AbstractValidator<AuditFieldPolicyCreateInput>
{
    /// <summary>
    /// PascalCase EntityType regex — starts uppercase ASCII, allows mixed-case
    /// alphanumerics inside, 3-64 characters total. Anchored. Catches the most
    /// common operator typo (<c>"solicitant"</c> lowercase) at validate time so
    /// the diff writer never silently fails to match a runtime type's
    /// <c>Type.Name</c>.
    /// </summary>
    internal const string EntityTypePattern = "^[A-Z][A-Za-z0-9]{2,63}$";

    /// <summary>Creates the validator.</summary>
    public AuditFieldPolicyInputValidator()
    {
        RuleFor(x => x.EntityType)
            .NotEmpty().WithMessage("EntityType is required.")
            .Matches(EntityTypePattern).WithMessage(
                "EntityType must be PascalCase ASCII (^[A-Z][A-Za-z0-9]{2,63}$) — it MUST match the CLR type's runtime Name exactly.");

        RuleFor(x => x.Severity)
            .NotEmpty().WithMessage("Severity is required.")
            .Must(AuditFieldPolicyValidationHelpers.SeverityStringIsValid)
            .WithMessage("Severity must be one of: Information, Notice, Sensitive, Critical.");

        RuleFor(x => x.Description)
            .MaximumLength(512).WithMessage("Description exceeds the 512-character cap.")
            .When(x => !string.IsNullOrWhiteSpace(x.Description));

        RuleFor(x => x.TrackedFields)
            .NotNull().WithMessage("TrackedFields must not be null (use [] for empty).");

        RuleFor(x => x.SuppressedFields)
            .NotNull().WithMessage("SuppressedFields must not be null (use [] for empty).");

        // Safeguard — "RequireAnyChange=true" demands at least one tracked field;
        // otherwise the policy would silently drop every audit row for the entity.
        RuleFor(x => x)
            .Must(AuditFieldPolicyValidationHelpers.TrackedFieldsNonEmptyWhenRequiringChange)
            .WithName(nameof(AuditFieldPolicyCreateInput.TrackedFields))
            .WithMessage(
                "TrackedFields must contain at least one entry when RequireAnyChange=true. "
                + "Set RequireAnyChange=false to always emit, or list the fields whose changes should trigger an audit row.");
    }
}

/// <summary>
/// R0183 / SEC 043 — validator for <see cref="AuditFieldPolicyUpdateInput"/>.
/// Mirrors <see cref="AuditFieldPolicyInputValidator"/> minus the immutable
/// <c>EntityType</c> rule.
/// </summary>
public sealed class AuditFieldPolicyUpdateInputValidator : AbstractValidator<AuditFieldPolicyUpdateInput>
{
    /// <summary>Creates the validator.</summary>
    public AuditFieldPolicyUpdateInputValidator()
    {
        RuleFor(x => x.Severity)
            .NotEmpty().WithMessage("Severity is required.")
            .Must(AuditFieldPolicyValidationHelpers.SeverityStringIsValid)
            .WithMessage("Severity must be one of: Information, Notice, Sensitive, Critical.");

        RuleFor(x => x.Description)
            .MaximumLength(512).WithMessage("Description exceeds the 512-character cap.")
            .When(x => !string.IsNullOrWhiteSpace(x.Description));

        RuleFor(x => x.TrackedFields)
            .NotNull().WithMessage("TrackedFields must not be null (use [] for empty).");

        RuleFor(x => x.SuppressedFields)
            .NotNull().WithMessage("SuppressedFields must not be null (use [] for empty).");

        RuleFor(x => x)
            .Must(input => AuditFieldPolicyValidationHelpers.TrackedFieldsNonEmptyWhenRequiringChangeUpdate(input))
            .WithName(nameof(AuditFieldPolicyUpdateInput.TrackedFields))
            .WithMessage(
                "TrackedFields must contain at least one entry when RequireAnyChange=true. "
                + "Set RequireAnyChange=false to always emit, or list the fields whose changes should trigger an audit row.");
    }
}

/// <summary>
/// Internal helpers shared by the field-policy create / update validators.
/// </summary>
internal static class AuditFieldPolicyValidationHelpers
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="severityString"/> parses to a defined
    /// <see cref="AuditSeverity"/> enum value (case-sensitive — operator runbooks
    /// reference PascalCase severity names).
    /// </summary>
    /// <param name="severityString">Caller-supplied severity name.</param>
    /// <returns><c>true</c> when the string is a valid severity name.</returns>
    internal static bool SeverityStringIsValid(string? severityString)
    {
        if (string.IsNullOrWhiteSpace(severityString))
        {
            return false;
        }
        return Enum.TryParse<AuditSeverity>(severityString, ignoreCase: false, out var parsed)
            && Enum.IsDefined(parsed);
    }

    /// <summary>
    /// Returns <c>true</c> when the create input is internally consistent —
    /// <c>RequireAnyChange=false</c> always passes, and
    /// <c>RequireAnyChange=true</c> requires at least one tracked field.
    /// </summary>
    /// <param name="input">Create payload.</param>
    /// <returns><c>true</c> when the combination is legal.</returns>
    internal static bool TrackedFieldsNonEmptyWhenRequiringChange(AuditFieldPolicyCreateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.RequireAnyChange)
        {
            return true;
        }
        return input.TrackedFields is { Count: > 0 };
    }

    /// <summary>
    /// Update-DTO sibling of
    /// <see cref="TrackedFieldsNonEmptyWhenRequiringChange(AuditFieldPolicyCreateInput)"/>.
    /// </summary>
    /// <param name="input">Update payload.</param>
    /// <returns><c>true</c> when the combination is legal.</returns>
    internal static bool TrackedFieldsNonEmptyWhenRequiringChangeUpdate(AuditFieldPolicyUpdateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.RequireAnyChange)
        {
            return true;
        }
        return input.TrackedFields is { Count: > 0 };
    }
}
