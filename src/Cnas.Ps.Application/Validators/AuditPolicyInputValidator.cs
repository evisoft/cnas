using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;
// AuditPolicy entity reference used by XML documentation.

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0182 / SEC 042 — validator for <see cref="AuditPolicyCreateInput"/>. Enforces the
/// natural-key shape, the regex-compiles invariant, the priority floor, and the
/// "suppression must be Information" safeguard documented on <see cref="AuditPolicy"/>.
/// </summary>
/// <remarks>
/// Pairs with <see cref="AuditPolicyUpdateInputValidator"/> — the two validators duplicate
/// the field-level rules for the fields they share. Pulling the shared rules into a
/// private static helper would add indirection without saving code; the duplication is
/// small enough that explicit per-DTO validators are cleaner.
/// </remarks>
public sealed class AuditPolicyInputValidator : AbstractValidator<AuditPolicyCreateInput>
{
    /// <summary>
    /// Stable natural-key shape: starts lowercase ASCII, allows dots and dashes inside,
    /// 3-80 characters total. Anchored.
    /// </summary>
    internal const string CodePattern = "^[a-z][a-z0-9.-]{2,79}$";

    /// <summary>Creates the validator.</summary>
    public AuditPolicyInputValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Code is required.")
            .Matches(CodePattern).WithMessage(
                "Code must be lowercase ASCII starting with a letter; allowed characters: a-z, 0-9, dot, dash; length 3-80.");

        RuleFor(x => x.Module)
            .NotEmpty().WithMessage("Module is required.")
            .MaximumLength(64).WithMessage("Module exceeds the 64-character cap.");

        RuleFor(x => x.Screen)
            .NotEmpty().WithMessage("Screen is required.")
            .MaximumLength(64).WithMessage("Screen exceeds the 64-character cap.");

        RuleFor(x => x.DataCategory)
            .MaximumLength(32).WithMessage("DataCategory exceeds the 32-character cap.")
            .When(x => !string.IsNullOrWhiteSpace(x.DataCategory));

        RuleFor(x => x.EventCodePattern)
            .NotEmpty().WithMessage("EventCodePattern is required.")
            .MaximumLength(256).WithMessage("EventCodePattern exceeds the 256-character cap.")
            .Must(AuditPolicyValidationHelpers.PatternCompiles)
            .WithMessage("EventCodePattern is not a valid .NET regex.");

        RuleFor(x => x.OverrideSeverity)
            .Must(AuditPolicyValidationHelpers.SeverityStringIsValid)
            .When(x => !string.IsNullOrWhiteSpace(x.OverrideSeverity))
            .WithMessage("OverrideSeverity must be one of: Information, Notice, Sensitive, Critical.");

        RuleFor(x => x.Priority)
            .GreaterThanOrEqualTo(0).WithMessage("Priority must be >= 0.");

        RuleFor(x => x.Description)
            .MaximumLength(512).WithMessage("Description exceeds the 512-character cap.")
            .When(x => !string.IsNullOrWhiteSpace(x.Description));

        // Safeguard — suppression is only legal for Information severity.
        RuleFor(x => x)
            .Must(AuditPolicyValidationHelpers.SuppressionMatchesInformation)
            .WithName(nameof(AuditPolicyCreateInput.SuppressAudit))
            .WithMessage(
                "SuppressAudit=true is permitted only when OverrideSeverity is null or Information. "
                + "Critical / Sensitive / Notice events must NEVER be suppressed.");
    }
}

/// <summary>
/// R0182 / SEC 042 — validator for <see cref="AuditPolicyUpdateInput"/>. Mirrors
/// <see cref="AuditPolicyInputValidator"/> minus the immutable <c>Code</c> rule.
/// </summary>
public sealed class AuditPolicyUpdateInputValidator : AbstractValidator<AuditPolicyUpdateInput>
{
    /// <summary>Creates the validator.</summary>
    public AuditPolicyUpdateInputValidator()
    {
        RuleFor(x => x.Module)
            .NotEmpty().WithMessage("Module is required.")
            .MaximumLength(64).WithMessage("Module exceeds the 64-character cap.");

        RuleFor(x => x.Screen)
            .NotEmpty().WithMessage("Screen is required.")
            .MaximumLength(64).WithMessage("Screen exceeds the 64-character cap.");

        RuleFor(x => x.DataCategory)
            .MaximumLength(32).WithMessage("DataCategory exceeds the 32-character cap.")
            .When(x => !string.IsNullOrWhiteSpace(x.DataCategory));

        RuleFor(x => x.EventCodePattern)
            .NotEmpty().WithMessage("EventCodePattern is required.")
            .MaximumLength(256).WithMessage("EventCodePattern exceeds the 256-character cap.")
            .Must(AuditPolicyValidationHelpers.PatternCompiles)
            .WithMessage("EventCodePattern is not a valid .NET regex.");

        RuleFor(x => x.OverrideSeverity)
            .Must(AuditPolicyValidationHelpers.SeverityStringIsValid)
            .When(x => !string.IsNullOrWhiteSpace(x.OverrideSeverity))
            .WithMessage("OverrideSeverity must be one of: Information, Notice, Sensitive, Critical.");

        RuleFor(x => x.Priority)
            .GreaterThanOrEqualTo(0).WithMessage("Priority must be >= 0.");

        RuleFor(x => x.Description)
            .MaximumLength(512).WithMessage("Description exceeds the 512-character cap.")
            .When(x => !string.IsNullOrWhiteSpace(x.Description));

        RuleFor(x => x)
            .Must(input => AuditPolicyValidationHelpers.SuppressionMatchesInformationUpdate(input))
            .WithName(nameof(AuditPolicyUpdateInput.SuppressAudit))
            .WithMessage(
                "SuppressAudit=true is permitted only when OverrideSeverity is null or Information. "
                + "Critical / Sensitive / Notice events must NEVER be suppressed.");
    }
}

/// <summary>
/// Internal helpers shared by the create / update validators. Lifted into a static class
/// so the regex-compile invariant + the suppression safeguard live in one place.
/// </summary>
internal static class AuditPolicyValidationHelpers
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="pattern"/> is a syntactically valid
    /// .NET regex. The compilation also catches obviously catastrophic patterns at
    /// validate-time (mismatched parens, unterminated character classes) so the
    /// resolver's hot path never blows up. Runtime DoS is still defended by the 50 ms
    /// per-match timeout in the resolver implementation.
    /// </summary>
    /// <param name="pattern">User-supplied regex pattern.</param>
    /// <returns><c>true</c> when the pattern compiles; <c>false</c> otherwise.</returns>
    internal static bool PatternCompiles(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            // Empty pattern is rejected by the NotEmpty rule; here we just don't crash.
            return false;
        }
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the suppression flag is compatible with the override
    /// severity. Suppression of Notice / Sensitive / Critical is forbidden — those
    /// events MUST land in the audit trail regardless of operator preference.
    /// </summary>
    /// <param name="input">Create payload under validation.</param>
    /// <returns><c>true</c> when the combination is legal; <c>false</c> otherwise.</returns>
    internal static bool SuppressionMatchesInformation(AuditPolicyCreateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.SuppressAudit)
        {
            return true;
        }
        return SuppressionLegalForSeverityString(input.OverrideSeverity);
    }

    /// <summary>
    /// Update-DTO sibling of <see cref="SuppressionMatchesInformation(AuditPolicyCreateInput)"/>.
    /// Duplicated because the two DTOs are unrelated record types — extracting a generic
    /// would add indirection for two fields.
    /// </summary>
    /// <param name="input">Update payload under validation.</param>
    /// <returns><c>true</c> when the combination is legal; <c>false</c> otherwise.</returns>
    internal static bool SuppressionMatchesInformationUpdate(AuditPolicyUpdateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.SuppressAudit)
        {
            return true;
        }
        return SuppressionLegalForSeverityString(input.OverrideSeverity);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="severityString"/> is recognised as a
    /// valid <see cref="AuditSeverity"/> name. Used by the OverrideSeverity rule on
    /// both create and update validators.
    /// </summary>
    /// <param name="severityString">Caller-supplied severity name; null/whitespace is rejected here.</param>
    /// <returns><c>true</c> when the string parses to a defined enum value.</returns>
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
    /// Returns <c>true</c> when the supplied severity string is legal for a
    /// suppression policy — i.e. null (preserves caller), <c>Information</c>, or an
    /// unrecognised string (which the OverrideSeverity rule will reject separately
    /// with a clearer message).
    /// </summary>
    /// <param name="severityString">Caller-supplied severity name; nullable.</param>
    /// <returns><c>true</c> when the combination is permitted.</returns>
    private static bool SuppressionLegalForSeverityString(string? severityString)
    {
        if (string.IsNullOrWhiteSpace(severityString))
        {
            return true;
        }
        // Recognised non-Information severities are illegal for suppression.
        if (Enum.TryParse<AuditSeverity>(severityString, ignoreCase: false, out var parsed))
        {
            return parsed == AuditSeverity.Information;
        }
        // Unrecognised string — defer to the OverrideSeverity rule's own error.
        return true;
    }
}
