using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0671 continuation / TOR CF 18.06 — FluentValidation rules for
/// <see cref="AccessScopeSolicitantBackfillInputDto"/>. Mirrors the constraint
/// surface documented on the DTO: a non-empty <c>RegionCode</c> matching the
/// canonical 1..16-char <c>UPPER</c>-prefix regex, the explicit-Sqid cap, and
/// the "at least one of (Filter, ExplicitSolicitantSqids)" rule.
/// </summary>
/// <remarks>
/// <para>
/// <b>QBE envelope.</b> The QBE filter itself is NOT validated here — the
/// <c>IQbeToLinqConverter</c> emits stable <c>QBE_*</c> error codes when a
/// malformed envelope reaches the service. Splitting the responsibility keeps
/// this validator small and avoids duplicating the converter contract.
/// </para>
/// </remarks>
public sealed class AccessScopeSolicitantBackfillInputValidator
    : AbstractValidator<AccessScopeSolicitantBackfillInputDto>
{
    /// <summary>Hard cap on explicit-Sqid list length (defends the back-fill quota).</summary>
    public const int MaxExplicitSqids = 5000;

    /// <summary>
    /// Canonical region-code regex: must start with an upper-case letter, may
    /// continue with up to 15 upper-case letters / digits / <c>_</c> / <c>-</c>.
    /// Total length cap = 16.
    /// </summary>
    public const string RegionCodeRegex = "^[A-Z][A-Z0-9_-]{0,15}$";

    private static readonly Regex RegionCodePattern = new(
        RegionCodeRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Creates the validator with the canonical rule set.</summary>
    public AccessScopeSolicitantBackfillInputValidator()
    {
        RuleFor(x => x.RegionCode)
            .NotEmpty().WithMessage("RegionCode must not be empty.")
            .Must(code => code is not null && RegionCodePattern.IsMatch(code))
            .WithMessage(
                $"RegionCode must match {RegionCodeRegex} (1..16 chars, starts with an upper-case letter).");

        RuleFor(x => x.ExplicitSolicitantSqids)
            .Must(list => list is null || list.Count <= MaxExplicitSqids)
            .WithMessage($"ExplicitSolicitantSqids must not exceed {MaxExplicitSqids} entries.");

        // At least one of Filter or ExplicitSolicitantSqids must be present.
        // We anchor the rule on the whole record because the failure is a relational
        // invariant rather than a single-property check.
        RuleFor(x => x)
            .Must(x => x.Filter is not null || x.ExplicitSolicitantSqids is not null)
            .WithName(nameof(AccessScopeSolicitantBackfillInputDto.Filter))
            .WithMessage(
                "Either Filter or ExplicitSolicitantSqids must be provided.");
    }
}

/// <summary>
/// R0671 continuation / TOR CF 18.06 — FluentValidation rules for
/// <see cref="AccessScopeApplicationBackfillInputDto"/>. Mirrors the sibling
/// <see cref="AccessScopeSolicitantBackfillInputValidator"/> for the
/// subdivision-code axis. Active-branch-code existence is checked at the service
/// layer (it needs a DB round-trip), not here.
/// </summary>
public sealed class AccessScopeApplicationBackfillInputValidator
    : AbstractValidator<AccessScopeApplicationBackfillInputDto>
{
    /// <summary>Hard cap on explicit-Sqid list length.</summary>
    public const int MaxExplicitSqids = 5000;

    /// <summary>
    /// Canonical subdivision-code regex: must start with an upper-case letter,
    /// may continue with up to 63 upper-case letters / digits / <c>.</c> /
    /// <c>_</c> / <c>-</c>. Total length cap = 64 (matches
    /// <c>CnasBranch.Code</c>'s persistence cap).
    /// </summary>
    public const string SubdivisionCodeRegex = "^[A-Z][A-Z0-9._-]{0,63}$";

    private static readonly Regex SubdivisionCodePattern = new(
        SubdivisionCodeRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Creates the validator with the canonical rule set.</summary>
    public AccessScopeApplicationBackfillInputValidator()
    {
        RuleFor(x => x.SubdivisionCode)
            .NotEmpty().WithMessage("SubdivisionCode must not be empty.")
            .Must(code => code is not null && SubdivisionCodePattern.IsMatch(code))
            .WithMessage(
                $"SubdivisionCode must match {SubdivisionCodeRegex} (1..64 chars, starts with an upper-case letter).");

        RuleFor(x => x.ExplicitApplicationSqids)
            .Must(list => list is null || list.Count <= MaxExplicitSqids)
            .WithMessage($"ExplicitApplicationSqids must not exceed {MaxExplicitSqids} entries.");

        RuleFor(x => x)
            .Must(x => x.Filter is not null || x.ExplicitApplicationSqids is not null)
            .WithName(nameof(AccessScopeApplicationBackfillInputDto.Filter))
            .WithMessage(
                "Either Filter or ExplicitApplicationSqids must be provided.");
    }
}
