using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0814 / R0815 — shared constants for the BASS-refund + payment-correction
/// validators. Centralised so the magic numbers don't drift across rule
/// sets.
/// </summary>
internal static class FinancialsValidatorShared
{
    /// <summary>Minimum permitted reason length.</summary>
    public const int ReasonMinLength = 3;

    /// <summary>Maximum permitted reason length.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>Maximum permitted authorisation-document-reference length.</summary>
    public const int AuthorisationReferenceMaxLength = 256;

    /// <summary>Maximum permitted Treasury-dispatch-reference length.</summary>
    public const int DispatchReferenceMaxLength = 64;

    /// <summary>Maximum permitted refund amount (MDL).</summary>
    public const decimal MaxAmount = 100_000_000m;

    /// <summary>Asserts the supplied month carries <c>Day == 1</c>.</summary>
    /// <param name="month">Candidate month.</param>
    /// <returns><c>true</c> when the day component is 1.</returns>
    public static bool IsFirstOfMonth(DateOnly month) => month.Day == 1;

    /// <summary>Asserts the supplied <paramref name="kind"/> parses to a known <see cref="PaymentCorrectionKind"/>.</summary>
    /// <param name="kind">Candidate kind string.</param>
    /// <returns><c>true</c> when parsing succeeds.</returns>
    public static bool IsValidCorrectionKind(string? kind)
        => kind is not null && Enum.TryParse<PaymentCorrectionKind>(kind, ignoreCase: false, out _);
}

/// <summary>
/// R0814 / BP 1.2-E — validates <see cref="BassRefundRequestInputDto"/>.
/// Enforces the payer Sqid shape, the month / amount bounds, and the
/// optional authorisation-document-reference length.
/// </summary>
public sealed class BassRefundRequestInputDtoValidator : AbstractValidator<BassRefundRequestInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public BassRefundRequestInputDtoValidator()
    {
        RuleFor(x => x.ContributorSqid)
            .NotEmpty().WithMessage("ContributorSqid is required.");

        RuleFor(x => x.RelatedMonth)
            .Must(FinancialsValidatorShared.IsFirstOfMonth)
            .WithMessage("RelatedMonth must be the first day of the month (Day == 1).");

        RuleFor(x => x.RefundAmount)
            .GreaterThan(0m).WithMessage("RefundAmount must be > 0.")
            .LessThanOrEqualTo(FinancialsValidatorShared.MaxAmount)
            .WithMessage($"RefundAmount cannot exceed {FinancialsValidatorShared.MaxAmount:0}.");

        RuleFor(x => x.AuthorisationDocumentReference!)
            .MaximumLength(FinancialsValidatorShared.AuthorisationReferenceMaxLength)
            .When(x => x.AuthorisationDocumentReference is not null)
            .WithMessage(
                $"AuthorisationDocumentReference cannot exceed {FinancialsValidatorShared.AuthorisationReferenceMaxLength} characters.");
    }
}

/// <summary>
/// R0814 / BP 1.2-E — validates <see cref="BassRefundIssueInputDto"/>.
/// Enforces the dispatch-reference length and non-emptiness rule.
/// </summary>
public sealed class BassRefundIssueInputDtoValidator : AbstractValidator<BassRefundIssueInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public BassRefundIssueInputDtoValidator()
    {
        RuleFor(x => x.TreasuryDispatchReference)
            .NotEmpty().WithMessage("TreasuryDispatchReference is required.")
            .MaximumLength(FinancialsValidatorShared.DispatchReferenceMaxLength)
            .WithMessage(
                $"TreasuryDispatchReference cannot exceed {FinancialsValidatorShared.DispatchReferenceMaxLength} characters.");
    }
}

/// <summary>
/// R0814 / BP 1.2-E — validates <see cref="BassRefundConfirmInputDto"/>.
/// Enforces the not-in-the-future rule on the supplied confirmation date.
/// The cutoff routes through the injected <see cref="ICnasTimeProvider"/>
/// per CLAUDE.md UTC-everywhere principle.
/// </summary>
public sealed class BassRefundConfirmInputDtoValidator : AbstractValidator<BassRefundConfirmInputDto>
{
    /// <summary>Wires the rule set against an injected <see cref="ICnasTimeProvider"/>.</summary>
    /// <param name="clock">UTC clock used to compute the not-in-the-future cutoff.</param>
    public BassRefundConfirmInputDtoValidator(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var todayUtc = DateOnly.FromDateTime(clock.UtcNow);

        RuleFor(x => x.ConfirmedDate)
            .Must(d => d <= todayUtc)
            .WithMessage("ConfirmedDate cannot be in the future.");
    }
}

/// <summary>
/// R0814 / BP 1.2-E — validates <see cref="BassRefundCancelInputDto"/>.
/// Enforces the standard 3..500-char reason shape.
/// </summary>
public sealed class BassRefundCancelInputDtoValidator : AbstractValidator<BassRefundCancelInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public BassRefundCancelInputDtoValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(FinancialsValidatorShared.ReasonMinLength)
            .WithMessage($"Reason must be at least {FinancialsValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(FinancialsValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason cannot exceed {FinancialsValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R0815 / BP 1.2-F — validates
/// <see cref="PaymentCorrectionCreateInputDto"/>. Enforces the receipt Sqid
/// shape, the kind parses to <see cref="PaymentCorrectionKind"/>, the
/// per-kind required-field invariants, and the optional redirect-month is
/// day=1.
/// </summary>
public sealed class PaymentCorrectionCreateInputDtoValidator : AbstractValidator<PaymentCorrectionCreateInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public PaymentCorrectionCreateInputDtoValidator()
    {
        RuleFor(x => x.OriginalReceiptSqid)
            .NotEmpty().WithMessage("OriginalReceiptSqid is required.");

        RuleFor(x => x.Kind)
            .NotEmpty().WithMessage("Kind is required.")
            .Must(FinancialsValidatorShared.IsValidCorrectionKind)
            .WithMessage("Kind must be one of Reverse, RedirectToPayer, RedirectToMonth, AdjustAmount.");

        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(FinancialsValidatorShared.ReasonMinLength)
            .WithMessage($"Reason must be at least {FinancialsValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(FinancialsValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason cannot exceed {FinancialsValidatorShared.ReasonMaxLength} characters.");

        // RedirectToPayer requires RedirectedToContributorSqid.
        RuleFor(x => x.RedirectedToContributorSqid)
            .NotEmpty().When(x => x.Kind == nameof(PaymentCorrectionKind.RedirectToPayer))
            .WithMessage("RedirectedToContributorSqid is required when Kind is RedirectToPayer.");

        // RedirectToMonth requires RedirectedToMonth with Day == 1.
        RuleFor(x => x.RedirectedToMonth)
            .NotNull().When(x => x.Kind == nameof(PaymentCorrectionKind.RedirectToMonth))
            .WithMessage("RedirectedToMonth is required when Kind is RedirectToMonth.");
        RuleFor(x => x.RedirectedToMonth!.Value)
            .Must(FinancialsValidatorShared.IsFirstOfMonth)
            .When(x => x.RedirectedToMonth.HasValue)
            .WithMessage("RedirectedToMonth must be the first day of the month (Day == 1).");

        // AdjustAmount requires positive AdjustedAmount.
        RuleFor(x => x.AdjustedAmount)
            .NotNull().When(x => x.Kind == nameof(PaymentCorrectionKind.AdjustAmount))
            .WithMessage("AdjustedAmount is required when Kind is AdjustAmount.");
        RuleFor(x => x.AdjustedAmount!.Value)
            .GreaterThan(0m)
            .When(x => x.AdjustedAmount.HasValue)
            .WithMessage("AdjustedAmount must be > 0.")
            .LessThanOrEqualTo(FinancialsValidatorShared.MaxAmount)
            .When(x => x.AdjustedAmount.HasValue)
            .WithMessage($"AdjustedAmount cannot exceed {FinancialsValidatorShared.MaxAmount:0}.");
    }
}

/// <summary>
/// R0815 / BP 1.2-F — validates <see cref="PaymentCorrectionCancelInputDto"/>.
/// Enforces the standard 3..500-char reason shape.
/// </summary>
public sealed class PaymentCorrectionCancelInputDtoValidator : AbstractValidator<PaymentCorrectionCancelInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public PaymentCorrectionCancelInputDtoValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(FinancialsValidatorShared.ReasonMinLength)
            .WithMessage($"Reason must be at least {FinancialsValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(FinancialsValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason cannot exceed {FinancialsValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R0817 / BP 1.2-H — validates
/// <see cref="PenaltyRepaymentCreatePlanInputDto"/>. Enforces the penalty
/// Sqid shape, the inclusive (2..36) installment-count band, and the
/// first-installment due date must be ≥ today (computed via the injected
/// <see cref="ICnasTimeProvider"/>).
/// </summary>
public sealed class PenaltyRepaymentCreatePlanInputDtoValidator
    : AbstractValidator<PenaltyRepaymentCreatePlanInputDto>
{
    /// <summary>Minimum permitted installment count.</summary>
    public const int MinInstallments = 2;

    /// <summary>Maximum permitted installment count.</summary>
    public const int MaxInstallments = 36;

    /// <summary>Wires the rule set against an injected <see cref="ICnasTimeProvider"/>.</summary>
    /// <param name="clock">UTC clock used to compute the today-cutoff.</param>
    public PenaltyRepaymentCreatePlanInputDtoValidator(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var todayUtc = DateOnly.FromDateTime(clock.UtcNow);

        RuleFor(x => x.LatePaymentPenaltySqid)
            .NotEmpty().WithMessage("LatePaymentPenaltySqid is required.");

        RuleFor(x => x.InstallmentCount)
            .InclusiveBetween(MinInstallments, MaxInstallments)
            .WithMessage($"InstallmentCount must be between {MinInstallments} and {MaxInstallments}.");

        RuleFor(x => x.FirstInstallmentDueDate)
            .Must(d => d >= todayUtc)
            .WithMessage("FirstInstallmentDueDate cannot be in the past.");
    }
}

/// <summary>
/// R0817 / BP 1.2-H — validates
/// <see cref="PenaltyRepaymentRegisterPaymentInputDto"/>. Enforces the
/// paid-date not-in-the-future rule and the positive-amount rule. The
/// today-cutoff routes through the injected
/// <see cref="ICnasTimeProvider"/>.
/// </summary>
public sealed class PenaltyRepaymentRegisterPaymentInputDtoValidator
    : AbstractValidator<PenaltyRepaymentRegisterPaymentInputDto>
{
    /// <summary>Wires the rule set against an injected <see cref="ICnasTimeProvider"/>.</summary>
    /// <param name="clock">UTC clock used to compute the today-cutoff.</param>
    public PenaltyRepaymentRegisterPaymentInputDtoValidator(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var todayUtc = DateOnly.FromDateTime(clock.UtcNow);

        RuleFor(x => x.PaidDate)
            .Must(d => d <= todayUtc)
            .WithMessage("PaidDate cannot be in the future.");

        RuleFor(x => x.PaidAmount)
            .GreaterThan(0m).WithMessage("PaidAmount must be > 0.")
            .LessThanOrEqualTo(FinancialsValidatorShared.MaxAmount)
            .WithMessage($"PaidAmount cannot exceed {FinancialsValidatorShared.MaxAmount:0}.");
    }
}

/// <summary>
/// R0817 / BP 1.2-H — validates
/// <see cref="PenaltyRepaymentCancelPlanInputDto"/>. Enforces the standard
/// 3..500-char reason shape.
/// </summary>
public sealed class PenaltyRepaymentCancelPlanInputDtoValidator
    : AbstractValidator<PenaltyRepaymentCancelPlanInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public PenaltyRepaymentCancelPlanInputDtoValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(FinancialsValidatorShared.ReasonMinLength)
            .WithMessage($"Reason must be at least {FinancialsValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(FinancialsValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason cannot exceed {FinancialsValidatorShared.ReasonMaxLength} characters.");
    }
}
