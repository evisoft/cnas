using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>Validates search/QBE inputs (UC03, UC12).</summary>
public sealed class SearchRequestValidator : AbstractValidator<SearchRequest>
{
    /// <summary>Creates the validator.</summary>
    public SearchRequestValidator()
    {
        RuleFor(x => x.Page.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.Page.PageSize).InclusiveBetween(1, 200);
        RuleFor(x => x.Query).MaximumLength(256).When(x => x.Query is not null);
        RuleFor(x => x.Mask).MaximumLength(128).When(x => x.Mask is not null);
    }
}
