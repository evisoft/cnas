using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R1504 / TOR §3.7-E — validates a <see cref="PaymentSuspensionInputDto"/>
/// before it crosses the API boundary into the suspend/resume service.
/// </summary>
/// <remarks>
/// Constraints:
/// <list type="bullet">
///   <item><c>Reason</c> is between 3 and 500 characters (mirrors the
///   <c>RecoveryDecisionInputValidator</c> contract).</item>
/// </list>
/// </remarks>
public sealed class PaymentSuspensionInputValidator : AbstractValidator<PaymentSuspensionInputDto>
{
    /// <summary>Minimum length of the suspension / resume reason.</summary>
    public const int ReasonMinLength = 3;

    /// <summary>Maximum length of the suspension / resume reason.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>Builds the rule set.</summary>
    public PaymentSuspensionInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty()
            .WithMessage("Reason is required.")
            .MinimumLength(ReasonMinLength)
            .WithMessage($"Reason must be at least {ReasonMinLength} characters.")
            .MaximumLength(ReasonMaxLength)
            .WithMessage($"Reason cannot exceed {ReasonMaxLength} characters.");
    }
}
