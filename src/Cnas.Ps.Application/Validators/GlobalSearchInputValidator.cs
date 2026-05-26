using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0160 / R0161 / TOR CF 03.03 — FluentValidation rules for
/// <see cref="GlobalSearchInputDto"/>. Pins the free-text query budget (1..256
/// chars, no leading/trailing whitespace) and the paging caps (Skip ≥ 0,
/// Take ∈ [1, 100]) as well as the domain-allow-list (entries must appear in
/// <see cref="GlobalSearchDomains.All"/> after case-folding).
/// </summary>
/// <remarks>
/// The validator is intentionally lightweight — it does not consult the database
/// and does not attempt to "auto-fix" malformed input. Service-layer guards still
/// short-circuit on the same conditions so a controller that forgets to invoke
/// the validator still surfaces a clean <c>VALIDATION_FAILED</c> error.
/// </remarks>
public sealed class GlobalSearchInputValidator : AbstractValidator<GlobalSearchInputDto>
{
    /// <summary>Maximum length of the free-text query.</summary>
    public const int MaxQueryLength = 256;

    /// <summary>Hard server-side cap on <see cref="GlobalSearchInputDto.Take"/>.</summary>
    public const int MaxTake = 100;

    /// <summary>Creates the validator with the canonical rule set.</summary>
    public GlobalSearchInputValidator()
    {
        // Query: required, non-whitespace, ≤ MaxQueryLength, no leading/trailing
        // whitespace (we reject "  alpha" rather than silently trimming so callers
        // see the malformed payload explicitly).
        RuleFor(x => x.Query)
            .NotEmpty().WithMessage("Query is required.")
            .Must(q => q is not null && q.Length <= MaxQueryLength)
                .WithMessage($"Query must be at most {MaxQueryLength} characters.")
            .Must(q => q is not null && q == q.Trim())
                .WithMessage("Query must not have leading or trailing whitespace.");

        // Domains: each entry (case-folded) must appear in the known catalogue.
        // Empty / null is legal — it means "search every domain".
        RuleFor(x => x.Domains)
            .Must(list => list is null || list.All(IsKnownDomain))
            .WithMessage(
                "Each Domains entry must be one of: applications, contributors, insured-persons, documents, dossiers.");

        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Skip must be greater than or equal to 0.");

        RuleFor(x => x.Take)
            .GreaterThan(0).WithMessage("Take must be greater than 0.")
            .LessThanOrEqualTo(MaxTake)
            .WithMessage($"Take must not exceed the server-side cap of {MaxTake}.");
    }

    /// <summary>
    /// Returns <see langword="true"/> when the supplied raw domain code is a
    /// member of <see cref="GlobalSearchDomains.All"/> after case-folding.
    /// </summary>
    /// <param name="domain">Raw user-supplied domain code.</param>
    /// <returns>True if recognised; false otherwise.</returns>
    public static bool IsKnownDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return false;
        }

        foreach (var canonical in GlobalSearchDomains.All)
        {
            if (string.Equals(canonical, domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
