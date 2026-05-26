using Cnas.Ps.Application.PublicCatalog;
using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0502 / R0504 / R0505 — FluentValidation rules for
/// <see cref="PublicCatalogListQueryDto"/>. Enforced at the controller boundary
/// via the standard MVC model validation pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rules:</b>
/// <list type="bullet">
///   <item><see cref="PublicCatalogListQueryDto.Sort"/> MUST parse to a
///   <see cref="PublicCatalogSortOptions"/> enum value (case-insensitive).</item>
///   <item><see cref="PublicCatalogListQueryDto.Take"/> MUST be in <c>[1, 200]</c>.</item>
///   <item><see cref="PublicCatalogListQueryDto.Skip"/> MUST be <c>&gt;= 0</c>.</item>
///   <item><see cref="PublicCatalogListQueryDto.Q"/> MUST be <c>&lt;= 200</c> chars
///   when supplied; null / empty bypass the rule.</item>
/// </list>
/// </para>
/// <para>
/// <b>Boundary discipline.</b> Validation enforces the wire-side contract; the
/// service layer additionally clamps <see cref="PublicCatalogListQueryDto.Take"/>
/// to the same upper bound as a defense-in-depth check (so a programmer who
/// bypasses the validator can't widen the cap).
/// </para>
/// </remarks>
public sealed class PublicCatalogListQueryValidator : AbstractValidator<PublicCatalogListQueryDto>
{
    /// <summary>The hard upper bound on the page-size cap (mirrors the service-layer clamp).</summary>
    public const int MaxTake = 200;

    /// <summary>The maximum length of the free-text query string.</summary>
    public const int MaxQueryLength = 200;

    /// <summary>Constructs the validator and registers the rule set.</summary>
    public PublicCatalogListQueryValidator()
    {
        // Sort must parse to the enum (case-insensitive). FluentValidation evaluates
        // the predicate against the property value AFTER its own null-guard, so we
        // additionally bail early when the caller sends a null Sort to avoid a
        // false positive — the inbound DTO supplies the default "Relevance".
        RuleFor(x => x.Sort)
            .Must(value =>
                !string.IsNullOrWhiteSpace(value)
                && Enum.TryParse<PublicCatalogSortOptions>(value, ignoreCase: true, out _))
            .WithMessage("Sort must be one of: Relevance, Alphabetical, Created, Updated.");

        // Numeric pagination bounds. Skip is unbounded above (the budget guard
        // protects the registry); Take is clamped both client and server side.
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Take).InclusiveBetween(1, MaxTake);

        // Free-text query length. Long queries are not a security risk (the SQL
        // pipeline parameterises everything) but a 10 KB Q value is almost
        // certainly an injection-style probe — the cap fails fast.
        RuleFor(x => x.Q)
            .MaximumLength(MaxQueryLength)
            .When(x => !string.IsNullOrEmpty(x.Q));
    }
}
