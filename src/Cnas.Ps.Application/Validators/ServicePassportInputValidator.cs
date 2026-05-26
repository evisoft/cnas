using Cnas.Ps.Contracts;
using Cnas.Ps.Application.UseCases;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>Validates ServicePassport upsert input (UC15).</summary>
public sealed class ServicePassportInputValidator : AbstractValidator<ServicePassportInput>
{
    /// <summary>Creates the validator.</summary>
    public ServicePassportInputValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(64).Matches("^[A-Z0-9_\\-]+$")
            .WithMessage("Code must be uppercase letters, digits, dashes or underscores.");
        RuleFor(x => x.NameRo).NotEmpty().MaximumLength(256);
        RuleFor(x => x.DescriptionRo).NotEmpty();
        RuleFor(x => x.WorkflowCode).NotEmpty().MaximumLength(64);
        RuleFor(x => x.MaxProcessingDays).InclusiveBetween(1, 365);
        RuleFor(x => x.FormSchemaJson).NotEmpty();
        RuleFor(x => x.DecisionRulesJson).NotNull()
            .WithMessage("DecisionRulesJson must not be null; supply '{}' for an empty rule-set.");
    }
}
