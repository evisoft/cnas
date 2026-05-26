using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0911 / BP 2.2-B — shared constants for the Treasury payment-receipt
/// validators. Centralised so the magic numbers don't drift across the rule
/// sets.
/// </summary>
internal static class TreasuryValidatorShared
{
    /// <summary>Maximum permitted Treasury reference-number length.</summary>
    public const int ReferenceMaxLength = 64;

    /// <summary>Maximum permitted amount per receipt (MDL).</summary>
    public const decimal MaxAmountReceived = 100_000_000m;

    /// <summary>
    /// Asserts the reporting month carries <c>Day == 1</c> per the entity
    /// contracts.
    /// </summary>
    /// <param name="month">Candidate month.</param>
    /// <returns><c>true</c> when the day component is 1.</returns>
    public static bool IsFirstOfMonth(DateOnly month) => month.Day == 1;
}

/// <summary>
/// R0911 / BP 2.2-B — validates
/// <see cref="TreasuryPaymentReceiptImportInputDto"/>. Enforces the
/// natural-key inputs (reference number, first-of-month), the amount bounds,
/// and the payer Sqid shape.
/// </summary>
public sealed class TreasuryPaymentReceiptImportInputDtoValidator
    : AbstractValidator<TreasuryPaymentReceiptImportInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public TreasuryPaymentReceiptImportInputDtoValidator()
    {
        RuleFor(x => x.TreasuryReferenceNumber)
            .NotEmpty().WithMessage("TreasuryReferenceNumber is required.")
            .MinimumLength(1)
            .MaximumLength(TreasuryValidatorShared.ReferenceMaxLength)
            .WithMessage($"TreasuryReferenceNumber must be 1..{TreasuryValidatorShared.ReferenceMaxLength} characters.");

        RuleFor(x => x.PayerContributorSqid)
            .NotEmpty().WithMessage("PayerContributorSqid is required.");

        RuleFor(x => x.ReportingMonth)
            .Must(TreasuryValidatorShared.IsFirstOfMonth)
            .WithMessage("ReportingMonth must be the first day of the month (Day == 1).");

        RuleFor(x => x.AmountReceived)
            .GreaterThan(0m)
            .WithMessage("AmountReceived must be > 0.")
            .LessThanOrEqualTo(TreasuryValidatorShared.MaxAmountReceived)
            .WithMessage($"AmountReceived cannot exceed {TreasuryValidatorShared.MaxAmountReceived:0}.");
    }
}
