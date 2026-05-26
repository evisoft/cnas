using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0922 / TOR Annex 2 §8.2.4 — FluentValidation rules for
/// <see cref="Pre1999StagiuInputDto"/>. Enforces the pre-1999 invariant on the
/// date range plus the normalised Years/Months/Days numeric bounds.
/// </summary>
public sealed class Pre1999StagiuInputValidator : AbstractValidator<Pre1999StagiuInputDto>
{
    /// <summary>Last calendar day before the 01.01.1999 transition.</summary>
    public static readonly DateOnly Pre1999CutOff = new(1998, 12, 31);

    /// <summary>Constructs the validator with the documented rule set.</summary>
    public Pre1999StagiuInputValidator()
    {
        RuleFor(x => x.FromDate)
            .LessThanOrEqualTo(Pre1999CutOff)
            .WithMessage($"FromDate must be on or before {Pre1999CutOff:yyyy-MM-dd} (pre-1999 invariant).");

        RuleFor(x => x.ToDate)
            .LessThanOrEqualTo(Pre1999CutOff)
            .WithMessage($"ToDate must be on or before {Pre1999CutOff:yyyy-MM-dd} (pre-1999 invariant).");

        RuleFor(x => x)
            .Must(x => x.FromDate <= x.ToDate)
            .WithMessage("FromDate must be on or before ToDate.")
            .OverridePropertyName(nameof(Pre1999StagiuInputDto.FromDate));

        RuleFor(x => x.Years)
            .InclusiveBetween(0, 70)
            .WithMessage("Years must be in the range [0, 70].");
        RuleFor(x => x.Months)
            .InclusiveBetween(0, 11)
            .WithMessage("Months must be in the range [0, 11].");
        RuleFor(x => x.Days)
            .InclusiveBetween(0, 30)
            .WithMessage("Days must be in the range [0, 30].");

        RuleFor(x => x.Source).MaximumLength(200);
        RuleFor(x => x.Notes).MaximumLength(500);
    }
}
