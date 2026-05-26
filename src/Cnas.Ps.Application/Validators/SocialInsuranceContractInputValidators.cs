using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0912 / BP 2.2-C — shared constants for the social-insurance contract
/// validators (issue / modify / terminate). Centralised so the magic numbers
/// don't drift across the rule sets.
/// </summary>
internal static class SocialInsuranceContractValidatorShared
{
    /// <summary>Maximum permitted contract-number length.</summary>
    public const int ContractNumberMaxLength = 50;

    /// <summary>Minimum permitted reason length.</summary>
    public const int ReasonMinLength = 3;

    /// <summary>Maximum permitted reason length.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>Maximum permitted monthly contribution amount (MDL).</summary>
    public const decimal MaxMonthlyContribution = 1_000_000m;

    /// <summary>Maximum permitted counterparty-name length.</summary>
    public const int CounterpartyMaxLength = 200;

    /// <summary>
    /// Asserts the input contains only ASCII characters. The contract number
    /// is a structured reference — Unicode/diacritics in the column would
    /// break case-insensitive equality lookups against paper records.
    /// </summary>
    /// <param name="value">Candidate string.</param>
    /// <returns><c>true</c> when every char is ≤ 0x7F.</returns>
    public static bool IsAscii(string? value)
    {
        if (value is null)
        {
            return true;
        }
        foreach (var ch in value)
        {
            if (ch > 0x7F)
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>
/// R0912 / BP 2.2-C — validates <see cref="SocialInsuranceContractIssueDto"/>.
/// Enforces the contract-number shape (ASCII, length), date-window
/// consistency, money bounds, and the operator-rationale length.
/// </summary>
public sealed class SocialInsuranceContractIssueDtoValidator
    : AbstractValidator<SocialInsuranceContractIssueDto>
{
    /// <summary>Builds the rule set.</summary>
    public SocialInsuranceContractIssueDtoValidator()
    {
        RuleFor(x => x.ContributorSqid)
            .NotEmpty().WithMessage("ContributorSqid is required.");

        RuleFor(x => x.ContractNumber)
            .NotEmpty().WithMessage("ContractNumber is required.")
            .MinimumLength(1)
            .MaximumLength(SocialInsuranceContractValidatorShared.ContractNumberMaxLength)
            .WithMessage($"ContractNumber must be 1..{SocialInsuranceContractValidatorShared.ContractNumberMaxLength} characters.")
            .Must(SocialInsuranceContractValidatorShared.IsAscii)
            .WithMessage("ContractNumber must contain only ASCII characters.");

        RuleFor(x => x)
            .Must(x => !x.ContractEndDate.HasValue || x.ContractEndDate.Value > x.ContractStartDate)
            .WithMessage("ContractEndDate must be strictly after ContractStartDate when supplied.");

        RuleFor(x => x.MonthlyContributionAmount)
            .GreaterThanOrEqualTo(0m).WithMessage("MonthlyContributionAmount must be ≥ 0.")
            .LessThanOrEqualTo(SocialInsuranceContractValidatorShared.MaxMonthlyContribution)
            .WithMessage($"MonthlyContributionAmount cannot exceed {SocialInsuranceContractValidatorShared.MaxMonthlyContribution:0}.");

        RuleFor(x => x.CounterpartyName!)
            .MaximumLength(SocialInsuranceContractValidatorShared.CounterpartyMaxLength)
            .When(x => x.CounterpartyName is not null)
            .WithMessage($"CounterpartyName cannot exceed {SocialInsuranceContractValidatorShared.CounterpartyMaxLength} characters.");

        RuleFor(x => x.ChangeReason)
            .NotEmpty().WithMessage("ChangeReason is required.")
            .MinimumLength(SocialInsuranceContractValidatorShared.ReasonMinLength)
            .MaximumLength(SocialInsuranceContractValidatorShared.ReasonMaxLength)
            .WithMessage($"ChangeReason must be {SocialInsuranceContractValidatorShared.ReasonMinLength}..{SocialInsuranceContractValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R0912 / BP 2.2-C — validates
/// <see cref="SocialInsuranceContractModifyDto"/>. Optional fields preserve
/// the existing value when null; supplied values are bounds-checked.
/// </summary>
public sealed class SocialInsuranceContractModifyDtoValidator
    : AbstractValidator<SocialInsuranceContractModifyDto>
{
    /// <summary>Builds the rule set.</summary>
    public SocialInsuranceContractModifyDtoValidator()
    {
        RuleFor(x => x.ContractNumber)
            .NotEmpty().WithMessage("ContractNumber is required.")
            .MinimumLength(1)
            .MaximumLength(SocialInsuranceContractValidatorShared.ContractNumberMaxLength)
            .WithMessage($"ContractNumber must be 1..{SocialInsuranceContractValidatorShared.ContractNumberMaxLength} characters.")
            .Must(SocialInsuranceContractValidatorShared.IsAscii)
            .WithMessage("ContractNumber must contain only ASCII characters.");

        // When both start and end are supplied (rare on modify) cross-check them.
        RuleFor(x => x)
            .Must(x => !x.ContractStartDate.HasValue
                || !x.ContractEndDate.HasValue
                || x.ContractEndDate.Value > x.ContractStartDate.Value)
            .WithMessage("ContractEndDate must be strictly after ContractStartDate when both supplied.");

        RuleFor(x => x.MonthlyContributionAmount!.Value)
            .GreaterThanOrEqualTo(0m)
            .LessThanOrEqualTo(SocialInsuranceContractValidatorShared.MaxMonthlyContribution)
            .When(x => x.MonthlyContributionAmount.HasValue)
            .WithMessage($"MonthlyContributionAmount must be within [0, {SocialInsuranceContractValidatorShared.MaxMonthlyContribution:0}].");

        RuleFor(x => x.CounterpartyName!)
            .MaximumLength(SocialInsuranceContractValidatorShared.CounterpartyMaxLength)
            .When(x => x.CounterpartyName is not null)
            .WithMessage($"CounterpartyName cannot exceed {SocialInsuranceContractValidatorShared.CounterpartyMaxLength} characters.");

        RuleFor(x => x.ChangeReason)
            .NotEmpty().WithMessage("ChangeReason is required.")
            .MinimumLength(SocialInsuranceContractValidatorShared.ReasonMinLength)
            .MaximumLength(SocialInsuranceContractValidatorShared.ReasonMaxLength)
            .WithMessage($"ChangeReason must be {SocialInsuranceContractValidatorShared.ReasonMinLength}..{SocialInsuranceContractValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R0912 / BP 2.2-C — validates
/// <see cref="SocialInsuranceContractTerminateDto"/>. Operator rationale is
/// required; <c>EffectiveDate</c> is intentionally unbounded here (future or
/// retroactive termination is permitted within R0912's scope).
/// </summary>
public sealed class SocialInsuranceContractTerminateDtoValidator
    : AbstractValidator<SocialInsuranceContractTerminateDto>
{
    /// <summary>Builds the rule set.</summary>
    public SocialInsuranceContractTerminateDtoValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(SocialInsuranceContractValidatorShared.ReasonMinLength)
            .MaximumLength(SocialInsuranceContractValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason must be {SocialInsuranceContractValidatorShared.ReasonMinLength}..{SocialInsuranceContractValidatorShared.ReasonMaxLength} characters.");
    }
}
