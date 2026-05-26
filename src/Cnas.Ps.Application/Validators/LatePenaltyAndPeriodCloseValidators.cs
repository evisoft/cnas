using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0819 / R0820 — shared validation constants for the late-penalty +
/// management-period-close inputs.
/// </summary>
internal static class LatePenaltyValidatorShared
{
    /// <summary>Minimum permitted reason length for waive / re-open operations.</summary>
    public const int ReasonMinLength = 3;

    /// <summary>Maximum permitted reason length for waive / re-open operations.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>Maximum permitted note length for close operations.</summary>
    public const int CloseNotesMaxLength = 1000;

    /// <summary>Asserts the supplied month carries <c>Day == 1</c>.</summary>
    /// <param name="month">Candidate month.</param>
    /// <returns><c>true</c> when the day component is 1.</returns>
    public static bool IsFirstOfMonth(DateOnly month) => month.Day == 1;
}

/// <summary>
/// R0819 / BP 1.2-J — validates
/// <see cref="LatePaymentPenaltyCalculateInputDto"/>. The
/// <see cref="LatePaymentPenaltyCalculateInputDto.UpToDate"/> must be ≥ the
/// reporting month start so penalty calculations cannot run against future
/// reporting periods.
/// </summary>
public sealed class LatePaymentPenaltyCalculateInputDtoValidator
    : AbstractValidator<LatePaymentPenaltyCalculateInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public LatePaymentPenaltyCalculateInputDtoValidator()
    {
        RuleFor(x => x.Month)
            .Must(LatePenaltyValidatorShared.IsFirstOfMonth)
            .WithMessage("Month must be the first day of the month (Day == 1).");

        RuleFor(x => x)
            .Must(x => x.UpToDate >= x.Month)
            .WithMessage("UpToDate must be greater than or equal to Month.");
    }
}

/// <summary>
/// R0819 / BP 1.2-J — validates
/// <see cref="LatePaymentPenaltyWaiveInputDto"/>.
/// </summary>
public sealed class LatePaymentPenaltyWaiveInputDtoValidator
    : AbstractValidator<LatePaymentPenaltyWaiveInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public LatePaymentPenaltyWaiveInputDtoValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(LatePenaltyValidatorShared.ReasonMinLength)
            .WithMessage($"Reason must be at least {LatePenaltyValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(LatePenaltyValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason cannot exceed {LatePenaltyValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R0820 / BP 1.2-K — validates <see cref="ManagementPeriodCloseInputDto"/>.
/// </summary>
public sealed class ManagementPeriodCloseInputDtoValidator
    : AbstractValidator<ManagementPeriodCloseInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public ManagementPeriodCloseInputDtoValidator()
    {
        RuleFor(x => x.Month)
            .Must(LatePenaltyValidatorShared.IsFirstOfMonth)
            .WithMessage("Month must be the first day of the month (Day == 1).");

        RuleFor(x => x.Notes!)
            .MaximumLength(LatePenaltyValidatorShared.CloseNotesMaxLength)
            .When(x => x.Notes is not null)
            .WithMessage($"Notes cannot exceed {LatePenaltyValidatorShared.CloseNotesMaxLength} characters.");
    }
}

/// <summary>
/// R0820 / BP 1.2-K — validates <see cref="ManagementPeriodReopenInputDto"/>.
/// </summary>
public sealed class ManagementPeriodReopenInputDtoValidator
    : AbstractValidator<ManagementPeriodReopenInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public ManagementPeriodReopenInputDtoValidator()
    {
        RuleFor(x => x.Month)
            .Must(LatePenaltyValidatorShared.IsFirstOfMonth)
            .WithMessage("Month must be the first day of the month (Day == 1).");

        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(LatePenaltyValidatorShared.ReasonMinLength)
            .WithMessage($"Reason must be at least {LatePenaltyValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(LatePenaltyValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason cannot exceed {LatePenaltyValidatorShared.ReasonMaxLength} characters.");
    }
}
