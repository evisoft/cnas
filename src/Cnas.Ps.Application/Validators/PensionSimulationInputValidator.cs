using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0514 / TOR CF 02.02 — validates <see cref="PensionSimulationInputDto"/> at
/// the API boundary per CLAUDE.md §2.5. The pension-projection simulator is a
/// citizen-facing surface, so the validator carries the full input-bounds
/// contract; the implementation layer trusts the values that survive this
/// gate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Range rationale.</b>
/// <list type="bullet">
///   <item><c>YearsOfService</c> 0..70 — covers every plausible career
///   length plus headroom for "what if" projections.</item>
///   <item><c>AverageMonthlyContributionBase</c> 0..1_000_000 — Moldovan-leu
///   nominal magnitudes plus headroom for high-earner scenarios.</item>
///   <item><c>CurrentAge</c> 14..120 — minimum employment age in Moldova
///   bounded against the human lifespan envelope.</item>
///   <item><c>RetirementAge</c> 50..75 when supplied — covers every statutory
///   variant the simulator might run against.</item>
///   <item><c>Gender</c> ∈ {"M", "F"} — drives the default retirement-age
///   substitution when <c>RetirementAge</c> is omitted.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class PensionSimulationInputValidator : AbstractValidator<PensionSimulationInputDto>
{
    /// <summary>Maximum permitted years of contributory service (inclusive).</summary>
    public const int MaxYearsOfService = 70;

    /// <summary>Maximum permitted monthly contribution base in MDL (inclusive).</summary>
    public const decimal MaxMonthlyContributionBase = 1_000_000m;

    /// <summary>Minimum permitted current age (inclusive).</summary>
    public const int MinCurrentAge = 14;

    /// <summary>Maximum permitted current age (inclusive).</summary>
    public const int MaxCurrentAge = 120;

    /// <summary>Minimum permitted retirement age when supplied (inclusive).</summary>
    public const int MinRetirementAge = 50;

    /// <summary>Maximum permitted retirement age when supplied (inclusive).</summary>
    public const int MaxRetirementAge = 75;

    /// <summary>Creates the validator with all field rules in place.</summary>
    public PensionSimulationInputValidator()
    {
        RuleFor(x => x.YearsOfService)
            .InclusiveBetween(0, MaxYearsOfService)
            .WithMessage($"YearsOfService must be between 0 and {MaxYearsOfService}.");

        RuleFor(x => x.AverageMonthlyContributionBase)
            .InclusiveBetween(0m, MaxMonthlyContributionBase)
            .WithMessage($"AverageMonthlyContributionBase must be between 0 and {MaxMonthlyContributionBase}.");

        RuleFor(x => x.CurrentAge)
            .InclusiveBetween(MinCurrentAge, MaxCurrentAge)
            .WithMessage($"CurrentAge must be between {MinCurrentAge} and {MaxCurrentAge}.");

        // RetirementAge is optional — when omitted the service substitutes the
        // gender default. When supplied it must fall inside the statutory
        // envelope so the projection cannot be skewed by absurd inputs.
        When(x => x.RetirementAge.HasValue, () =>
        {
            RuleFor(x => x.RetirementAge!.Value)
                .InclusiveBetween(MinRetirementAge, MaxRetirementAge)
                .WithMessage($"RetirementAge must be between {MinRetirementAge} and {MaxRetirementAge}.");
        });

        RuleFor(x => x.Gender)
            .NotEmpty()
            .WithMessage("Gender is required.")
            .Must(g => string.Equals(g, "M", StringComparison.Ordinal) || string.Equals(g, "F", StringComparison.Ordinal))
            .WithMessage("Gender must be 'M' or 'F'.");
    }
}
