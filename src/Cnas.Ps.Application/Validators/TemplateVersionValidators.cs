using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0132 / CF 17.18 — validator for <see cref="TemplateRollbackInputDto"/>. Enforces
/// the audit-friendly reason length window so the rollback row always carries an
/// investigable justification.
/// </summary>
public sealed class TemplateRollbackInputDtoValidator : AbstractValidator<TemplateRollbackInputDto>
{
    /// <summary>Minimum length of the rollback reason.</summary>
    public const int MinReasonLength = 3;

    /// <summary>Maximum length of the rollback reason.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>Wires the rule set.</summary>
    public TemplateRollbackInputDtoValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(MinReasonLength)
            .WithMessage($"Reason must be ≥ {MinReasonLength} characters.")
            .MaximumLength(MaxReasonLength)
            .WithMessage($"Reason must be ≤ {MaxReasonLength} characters.");
    }
}
