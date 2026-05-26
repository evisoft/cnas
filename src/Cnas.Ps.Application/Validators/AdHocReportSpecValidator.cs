using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0580 / TOR CF 09.02 — FluentValidation rules for
/// <see cref="AdHocReportSpecDto"/>. Enforces a non-empty / ≤ 20-column
/// projection, a known entity-set discriminator, and only known filter
/// operators. Column / order-by validity against the entity schema is the
/// builder's responsibility (it needs reflection into the entity type).
/// </summary>
public sealed class AdHocReportSpecValidator : AbstractValidator<AdHocReportSpecDto>
{
    /// <summary>Hard cap on the column-projection list length.</summary>
    public const int MaxColumns = 20;

    /// <summary>Creates the validator with the canonical rule set.</summary>
    public AdHocReportSpecValidator()
    {
        RuleFor(x => x.EntitySet)
            .NotEmpty().WithMessage("EntitySet must not be empty.")
            .Must(es => AdHocReportEntitySets.All.Contains(es))
            .WithMessage($"EntitySet must be one of {string.Join(", ", AdHocReportEntitySets.All)}.");

        RuleFor(x => x.Columns)
            .NotNull().WithMessage("Columns must not be null.")
            .Must(c => c is not null && c.Count > 0)
            .WithMessage("Columns must contain at least one entry.")
            .Must(c => c is null || c.Count <= MaxColumns)
            .WithMessage($"Columns must not exceed {MaxColumns} entries.");

        RuleFor(x => x.Filters)
            .NotNull().WithMessage("Filters must not be null (use an empty list to skip filtering).");

        RuleForEach(x => x.Filters).ChildRules(child =>
        {
            child.RuleFor(f => f.Field).NotEmpty().WithMessage("Filter.Field must not be empty.");
            child.RuleFor(f => f.Operator)
                .NotEmpty().WithMessage("Filter.Operator must not be empty.")
                .Must(op => AdHocReportOperators.All.Contains(op))
                .WithMessage(
                    $"Filter.Operator must be one of {string.Join(", ", AdHocReportOperators.All)}.");
            child.RuleFor(f => f.Value).NotNull().WithMessage("Filter.Value must not be null.");
        });
    }
}
