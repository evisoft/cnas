using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R1505 / TOR §3.7-F — validates a <see cref="RecoveryDecisionInputDto"/>
/// before it crosses the API boundary into the recovery-workflow service.
/// </summary>
/// <remarks>
/// Constraints:
/// <list type="bullet">
///   <item><c>SolicitantSqid</c> is non-empty.</item>
///   <item><c>Amount</c> is strictly positive and ≤ 100 000 000 MDL (matches the
///   claims-registry guard so the validator surface stays consistent across
///   financial-amount fields).</item>
///   <item><c>Reason</c> is between 3 and 500 characters (matches the
///   <c>ClaimValidatorShared.ReasonMinLength/MaxLength</c> contract).</item>
/// </list>
/// </remarks>
public sealed class RecoveryDecisionInputValidator : AbstractValidator<RecoveryDecisionInputDto>
{
    /// <summary>Minimum length of the recovery reason. Mirrors the claims-registry contract.</summary>
    public const int ReasonMinLength = 3;

    /// <summary>Maximum length of the recovery reason. Mirrors the claims-registry contract.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>Maximum recovery amount in MDL — defence-in-depth against fat-finger inputs.</summary>
    public const decimal MaxAmount = 100_000_000m;

    /// <summary>Builds the rule set.</summary>
    public RecoveryDecisionInputValidator()
    {
        RuleFor(x => x.SolicitantSqid)
            .NotEmpty()
            .WithMessage("SolicitantSqid is required.");

        RuleFor(x => x.Amount)
            .GreaterThan(0m)
            .WithMessage("Amount must be > 0.")
            .LessThanOrEqualTo(MaxAmount)
            .WithMessage($"Amount cannot exceed {MaxAmount:0}.");

        RuleFor(x => x.Reason)
            .NotEmpty()
            .WithMessage("Reason is required.")
            .MinimumLength(ReasonMinLength)
            .WithMessage($"Reason must be at least {ReasonMinLength} characters.")
            .MaximumLength(ReasonMaxLength)
            .WithMessage($"Reason cannot exceed {ReasonMaxLength} characters.");
    }
}
