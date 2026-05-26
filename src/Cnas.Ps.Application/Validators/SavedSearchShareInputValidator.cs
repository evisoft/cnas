using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0524 / TOR CF 03.06 — FluentValidation rules for <see cref="SavedSearchShareInput"/>.
/// Enforces the SharingScope parse, the group-code shape, and the cross-field invariant
/// between the chosen scope and the SharedWithGroupCode companion field.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope parse.</b> The wire surface carries the scope as the enum's stable
/// <c>ToString()</c> name (e.g. <c>"Private"</c> / <c>"Shared"</c> / <c>"Group"</c>) so
/// the validator must reject any string that does not round-trip through
/// <see cref="Enum.TryParse{T}(string, bool, out T)"/> with <c>ignoreCase: false</c>.
/// Case-insensitive parsing is intentionally disabled — a lowercase
/// <c>"private"</c> would silently bypass intent so we surface a clean
/// validation error instead.
/// </para>
/// <para>
/// <b>Group-code regex.</b> The pattern <c>^[a-z][a-z0-9._-]{1,63}$</c> matches the
/// kebab/dotted convention used by other CNAS group codes (lowercase, leading letter,
/// 2-64 chars total). Tightening the alphabet keeps the persisted column
/// resistant to typo-driven security exposure (a stray uppercase or whitespace
/// character cannot accidentally widen the audience because the row simply does
/// not match any caller's <c>UserProfile.Groups</c> entry).
/// </para>
/// <para>
/// <b>Cross-field invariant.</b> The validator enforces both halves of the contract:
/// <see cref="SavedSearchSharingScope.Group"/> REQUIRES a non-empty group code;
/// <see cref="SavedSearchSharingScope.Private"/> and
/// <see cref="SavedSearchSharingScope.Shared"/> REQUIRE the group code to be
/// <c>null</c>. Service-layer code therefore never has to second-guess the
/// pair when persisting.
/// </para>
/// </remarks>
public sealed class SavedSearchShareInputValidator : AbstractValidator<SavedSearchShareInput>
{
    /// <summary>
    /// Stable regex for the group-code shape. Lowercase initial letter, then
    /// lowercase letters / digits / dot / underscore / hyphen, 2-64 chars total.
    /// Anchored.
    /// </summary>
    public const string GroupCodePattern = "^[a-z][a-z0-9._-]{1,63}$";

    /// <summary>Creates the validator with the static rule set.</summary>
    public SavedSearchShareInputValidator()
    {
        // SharingScope must parse to a known enum value (case-sensitively — the wire
        // contract uses the canonical ToString() name).
        RuleFor(x => x.SharingScope)
            .Must(s => !string.IsNullOrWhiteSpace(s)
                && Enum.TryParse<SavedSearchSharingScope>(s, ignoreCase: false, out _))
            .WithMessage("SharingScope must be one of: Private, Shared, Group.");

        // Cross-field invariant: Group REQUIRES a non-empty SharedWithGroupCode that
        // matches the group-code regex.
        When(x => string.Equals(x.SharingScope, nameof(SavedSearchSharingScope.Group), StringComparison.Ordinal), () =>
        {
            RuleFor(x => x.SharedWithGroupCode)
                .NotEmpty()
                .WithMessage("SharedWithGroupCode is required when SharingScope = Group.");

            RuleFor(x => x.SharedWithGroupCode)
                .Matches(GroupCodePattern)
                .When(x => !string.IsNullOrEmpty(x.SharedWithGroupCode))
                .WithMessage("SharedWithGroupCode must match " + GroupCodePattern + ".");
        });

        // Cross-field invariant: Private / Shared MUST have a null SharedWithGroupCode.
        // The set of scopes covered here is the non-Group cases — including any value
        // the parse step rejects, but those already failed the scope rule above so the
        // double-failure is harmless and surfaces the cleaner SharingScope error first.
        When(x => !string.Equals(x.SharingScope, nameof(SavedSearchSharingScope.Group), StringComparison.Ordinal), () =>
        {
            RuleFor(x => x.SharedWithGroupCode)
                .Null()
                .WithMessage("SharedWithGroupCode must be null when SharingScope ≠ Group.");
        });
    }

    /// <summary>
    /// Test-friendly helper: returns <c>true</c> when the supplied group code matches
    /// <see cref="GroupCodePattern"/>. <c>null</c> / empty values return <c>false</c>
    /// because the helper is used by callers that have already established that
    /// some code is present.
    /// </summary>
    /// <param name="groupCode">Group code to check.</param>
    /// <returns><c>true</c> on a valid group code; <c>false</c> on null/empty/malformed input.</returns>
    public static bool GroupCodeShapeIsValid(string? groupCode)
    {
        if (string.IsNullOrEmpty(groupCode)) return false;
        return Regex.IsMatch(groupCode, GroupCodePattern, RegexOptions.CultureInvariant);
    }
}
