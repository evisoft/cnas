using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0507 / TOR CF 01.10 — FluentValidation rules for
/// <see cref="CaptchaVerifyInputDto"/>. Enforces presence and reasonable
/// length caps on both the opaque challenge token and the user-supplied
/// answer so the captcha service never sees pathological inputs.
/// </summary>
/// <remarks>
/// The validator is registered via
/// <c>AddValidatorsFromAssemblyContaining&lt;ApplicationAssemblyMarker&gt;</c>;
/// the controller injects it and invokes it before delegating to the
/// challenge service.
/// </remarks>
public sealed class CaptchaVerifyInputValidator : AbstractValidator<CaptchaVerifyInputDto>
{
    /// <summary>Maximum length accepted for the opaque challenge token.</summary>
    public const int MaxChallengeTokenLength = 128;

    /// <summary>Maximum length accepted for the user-supplied answer.</summary>
    public const int MaxAnswerLength = 64;

    /// <summary>Constructs the validator with the documented rule set.</summary>
    public CaptchaVerifyInputValidator()
    {
        RuleFor(x => x.ChallengeToken)
            .NotEmpty().WithMessage("ChallengeToken is required.")
            .MaximumLength(MaxChallengeTokenLength)
            .WithMessage($"ChallengeToken must be {MaxChallengeTokenLength} characters or fewer.");

        RuleFor(x => x.Answer)
            .NotEmpty().WithMessage("Answer is required.")
            .MaximumLength(MaxAnswerLength)
            .WithMessage($"Answer must be {MaxAnswerLength} characters or fewer.");
    }
}
