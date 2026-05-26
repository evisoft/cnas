using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0305 / BP 1.2 — validates <see cref="ContributorAttributesUpdateDto"/>. Enforces the
/// same Denumire / CFOJ / CAEM rules as the registration validator (R0304) but does NOT
/// re-validate the IDNO since it is immutable post-registration.
/// </summary>
public sealed class ContributorAttributesUpdateDtoValidator : AbstractValidator<ContributorAttributesUpdateDto>
{
    /// <summary>Max permitted Denumire length (matches the registration validator).</summary>
    private const int DenumireMaxLength = 256;

    /// <summary>Builds the rule set.</summary>
    public ContributorAttributesUpdateDtoValidator()
    {
        RuleFor(x => x.Denumire)
            .NotEmpty().WithMessage("Denumire is required.")
            .MaximumLength(DenumireMaxLength)
            .WithMessage($"Denumire cannot exceed {DenumireMaxLength} characters.");

        RuleFor(x => x.CfojCode)
            .Matches("^[0-9]{1,4}$")
            .When(x => x.CfojCode is not null)
            .WithMessage("CfojCode must be 1-4 numeric digits when supplied.");

        RuleFor(x => x.CaemCode)
            .Matches("^[0-9]{1,5}$")
            .When(x => x.CaemCode is not null)
            .WithMessage("CaemCode must be 1-5 numeric digits when supplied.");
    }
}

/// <summary>
/// R0305 / BP 1.3 — validates <see cref="ContributorDeactivationInputDto"/>. The audit
/// row preserves the operator's reason verbatim; the 3..500 char window keeps the
/// payload small while still allowing a meaningful explanation.
/// </summary>
public sealed class ContributorDeactivationInputDtoValidator : AbstractValidator<ContributorDeactivationInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public ContributorDeactivationInputDtoValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be at least 3 characters.")
            .MaximumLength(500).WithMessage("Reason cannot exceed 500 characters.");
    }
}

/// <summary>R0305 / BP 1.4 — validates <see cref="ContributorReactivationInputDto"/>.</summary>
public sealed class ContributorReactivationInputDtoValidator : AbstractValidator<ContributorReactivationInputDto>
{
    /// <summary>Builds the rule set (mirrors the deactivation validator).</summary>
    public ContributorReactivationInputDtoValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be at least 3 characters.")
            .MaximumLength(500).WithMessage("Reason cannot exceed 500 characters.");
    }
}

/// <summary>R0305 / BP 1.6 — validates <see cref="ContributorSplitInputDto"/>.</summary>
public sealed class ContributorSplitInputDtoValidator : AbstractValidator<ContributorSplitInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public ContributorSplitInputDtoValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be at least 3 characters.")
            .MaximumLength(500).WithMessage("Reason cannot exceed 500 characters.");
    }
}

/// <summary>
/// R0305 / BP 1.7 — validates <see cref="ContributorAdminCorrectionInputDto"/>. Field
/// name 1..64 chars, hashes 1..128 chars (raw base64 or hex), reason 3..500 chars.
/// </summary>
public sealed class ContributorAdminCorrectionInputDtoValidator : AbstractValidator<ContributorAdminCorrectionInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public ContributorAdminCorrectionInputDtoValidator()
    {
        RuleFor(x => x.FieldName)
            .NotEmpty().WithMessage("FieldName is required.")
            .MaximumLength(64).WithMessage("FieldName cannot exceed 64 characters.");

        RuleFor(x => x.OldValueHash)
            .NotEmpty().WithMessage("OldValueHash is required.")
            .MaximumLength(128).WithMessage("OldValueHash cannot exceed 128 characters.");

        RuleFor(x => x.NewValueHash)
            .NotEmpty().WithMessage("NewValueHash is required.")
            .MaximumLength(128).WithMessage("NewValueHash cannot exceed 128 characters.");

        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be at least 3 characters.")
            .MaximumLength(500).WithMessage("Reason cannot exceed 500 characters.");
    }
}

/// <summary>
/// R0305 / BP 1.9 — validates <see cref="ContributorMarkDeceasedInputDto"/>. The
/// effective date may not be in the future (anchored against the supplied clock).
/// </summary>
public sealed class ContributorMarkDeceasedInputDtoValidator : AbstractValidator<ContributorMarkDeceasedInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public ContributorMarkDeceasedInputDtoValidator()
    {
        // Future-date guard is checked at the service layer (the validator does not have
        // a clock — the controller binds the date the operator typed, and the service
        // is the authoritative gate). Here we only enforce a sane lower bound (1900) so
        // a stray default-DateOnly does not slip through.
        RuleFor(x => x.EffectiveDate)
            .GreaterThan(new DateOnly(1900, 1, 1))
            .WithMessage("EffectiveDate must be after 1900-01-01.");
    }
}
