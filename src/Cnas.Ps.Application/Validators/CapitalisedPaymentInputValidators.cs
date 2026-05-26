using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R1202 / TOR §3.4-C — shared constants and helpers for the
/// capitalised-payment validators. Centralised so the magic numbers do not
/// drift across rule sets.
/// </summary>
internal static class CapitalisedPaymentValidatorShared
{
    /// <summary>Minimum permitted reason / change-reason / note length.</summary>
    public const int ReasonMinLength = 3;

    /// <summary>Maximum permitted reason / change-reason / note length.</summary>
    public const int ReasonMaxLength = 1000;

    /// <summary>Maximum permitted debtor-name length.</summary>
    public const int NameMaxLength = 256;

    /// <summary>Minimum permitted debtor-name length.</summary>
    public const int NameMinLength = 3;

    /// <summary>Maximum permitted monetary amount (MDL).</summary>
    public const decimal MaxAmount = 100_000_000m;

    /// <summary>Maximum permitted annual discount rate (%).</summary>
    public const decimal MaxRatePercent = 30m;

    /// <summary>Maximum permitted beneficiary age in years.</summary>
    public const int MaxAgeYears = 110;

    /// <summary>Lower bound on the valuation date relative to today, in days.</summary>
    public const int ValuationDatePastDaysCutoff = -7;

    /// <summary>Upper bound on the valuation date relative to today, in days.</summary>
    public const int ValuationDateFutureDaysCutoff = 365;

    /// <summary>Compiled IDNP / IDNO regex — exactly 13 ASCII digits.</summary>
    public static readonly Regex ThirteenDigitsRegex = new(
        "^[0-9]{13}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    /// <summary>True when <paramref name="kind"/> parses to a known <see cref="CapitalisedPaymentObligationKind"/> name (case-sensitive).</summary>
    /// <param name="kind">Candidate enum-name string.</param>
    /// <returns>True when the value parses.</returns>
    public static bool IsValidObligationKind(string? kind) =>
        kind is not null && Enum.TryParse<CapitalisedPaymentObligationKind>(kind, ignoreCase: false, out _);

    /// <summary>True when <paramref name="sex"/> parses to a known <see cref="BeneficiarySex"/> name (case-sensitive).</summary>
    /// <param name="sex">Candidate enum-name string.</param>
    /// <returns>True when the value parses.</returns>
    public static bool IsValidSex(string? sex) =>
        sex is not null && Enum.TryParse<BeneficiarySex>(sex, ignoreCase: false, out _);

    /// <summary>True when <paramref name="status"/> parses to a known <see cref="CapitalisedPaymentRequestStatus"/> name (case-sensitive).</summary>
    /// <param name="status">Candidate enum-name string.</param>
    /// <returns>True when the value parses.</returns>
    public static bool IsValidStatus(string? status) =>
        status is not null && Enum.TryParse<CapitalisedPaymentRequestStatus>(status, ignoreCase: false, out _);
}

/// <summary>
/// R1202 — validates <see cref="CapitalisedPaymentRequestCreateInputDto"/>.
/// Enforces IDNP / IDNO format, the birth-date in-past + age range invariant,
/// monthly amount / discount-rate bounds, and the valuation-date cut-off.
/// Routes through <see cref="ICnasTimeProvider"/> so the date-based rules are
/// testable without mutating the system clock.
/// </summary>
public sealed class CapitalisedPaymentRequestCreateInputValidator
    : AbstractValidator<CapitalisedPaymentRequestCreateInputDto>
{
    /// <summary>Wires the rule set against an injected <see cref="ICnasTimeProvider"/>.</summary>
    /// <param name="clock">UTC clock used to compute the date-based cut-offs.</param>
    public CapitalisedPaymentRequestCreateInputValidator(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var todayUtc = DateOnly.FromDateTime(clock.UtcNow);
        var valuationFloor = todayUtc.AddDays(CapitalisedPaymentValidatorShared.ValuationDatePastDaysCutoff);
        var valuationCeil = todayUtc.AddDays(CapitalisedPaymentValidatorShared.ValuationDateFutureDaysCutoff);

        RuleFor(x => x.BeneficiaryIdnp)
            .NotEmpty().WithMessage("BeneficiaryIdnp is required.")
            .Must(idnp => CapitalisedPaymentValidatorShared.ThirteenDigitsRegex.IsMatch(idnp ?? string.Empty))
            .WithMessage("BeneficiaryIdnp must be exactly 13 digits.");

        RuleFor(x => x.BeneficiaryBirthDate)
            .Must(d => d <= todayUtc)
            .WithMessage("BeneficiaryBirthDate cannot be in the future.");

        RuleFor(x => x.BeneficiarySex)
            .NotEmpty().WithMessage("BeneficiarySex is required.")
            .Must(CapitalisedPaymentValidatorShared.IsValidSex)
            .WithMessage("BeneficiarySex must be one of Male, Female.");

        RuleFor(x => x.LiquidatedDebtorIdno)
            .NotEmpty().WithMessage("LiquidatedDebtorIdno is required.")
            .Must(idno => CapitalisedPaymentValidatorShared.ThirteenDigitsRegex.IsMatch(idno ?? string.Empty))
            .WithMessage("LiquidatedDebtorIdno must be exactly 13 digits.");

        RuleFor(x => x.LiquidatedDebtorName)
            .NotEmpty().WithMessage("LiquidatedDebtorName is required.")
            .MinimumLength(CapitalisedPaymentValidatorShared.NameMinLength)
            .WithMessage($"LiquidatedDebtorName must be at least {CapitalisedPaymentValidatorShared.NameMinLength} characters.")
            .MaximumLength(CapitalisedPaymentValidatorShared.NameMaxLength)
            .WithMessage($"LiquidatedDebtorName cannot exceed {CapitalisedPaymentValidatorShared.NameMaxLength} characters.");

        RuleFor(x => x.ObligationKind)
            .NotEmpty().WithMessage("ObligationKind is required.")
            .Must(CapitalisedPaymentValidatorShared.IsValidObligationKind)
            .WithMessage("ObligationKind must be one of IncapacityForWork, LossOfBreadwinner, OccupationalDisease.");

        RuleFor(x => x.MonthlyAmountMdl)
            .GreaterThan(0m).WithMessage("MonthlyAmountMdl must be > 0.")
            .LessThanOrEqualTo(CapitalisedPaymentValidatorShared.MaxAmount)
            .WithMessage($"MonthlyAmountMdl cannot exceed {CapitalisedPaymentValidatorShared.MaxAmount:0}.");

        RuleFor(x => x.ObligationStartDate)
            .Must(d => d <= todayUtc)
            .WithMessage("ObligationStartDate cannot be in the future.");

        RuleFor(x => x)
            .Must(x => x.ObligationStartDate > x.BeneficiaryBirthDate)
            .WithMessage("ObligationStartDate must be after BeneficiaryBirthDate.");

        RuleFor(x => x)
            .Must(x => !x.ObligationEndDate.HasValue || x.ObligationEndDate.Value >= x.ObligationStartDate)
            .WithMessage("ObligationEndDate must be greater than or equal to ObligationStartDate.");

        RuleFor(x => x.ValuationDate)
            .Must(d => d >= valuationFloor && d <= valuationCeil)
            .WithMessage(
                $"ValuationDate must fall within [{CapitalisedPaymentValidatorShared.ValuationDatePastDaysCutoff}, " +
                $"+{CapitalisedPaymentValidatorShared.ValuationDateFutureDaysCutoff}] days of today.");

        // Age-at-valuation in [0, 110].
        RuleFor(x => x)
            .Must(x =>
            {
                var ageMonths = MonthsBetween(x.BeneficiaryBirthDate, x.ValuationDate);
                var ageYears = ageMonths / 12.0m;
                return ageYears >= 0m && ageYears <= CapitalisedPaymentValidatorShared.MaxAgeYears;
            })
            .WithMessage("Computed age at ValuationDate must be in [0, 110] years.");

        RuleFor(x => x.LegalDiscountRatePercent)
            .GreaterThanOrEqualTo(0m).WithMessage("LegalDiscountRatePercent must be >= 0.")
            .LessThanOrEqualTo(CapitalisedPaymentValidatorShared.MaxRatePercent)
            .WithMessage($"LegalDiscountRatePercent cannot exceed {CapitalisedPaymentValidatorShared.MaxRatePercent}.");
    }

    /// <summary>
    /// Returns the count of whole months from <paramref name="start"/> to
    /// <paramref name="end"/>, clamped at zero. Drives the age-at-valuation
    /// computation inside the rule predicate.
    /// </summary>
    /// <param name="start">Earlier calendar date (e.g. birth date).</param>
    /// <param name="end">Later calendar date (e.g. valuation date).</param>
    /// <returns>Difference in whole months (≥ 0).</returns>
    internal static int MonthsBetween(DateOnly start, DateOnly end)
    {
        if (end < start)
        {
            return 0;
        }
        var months = ((end.Year - start.Year) * 12) + (end.Month - start.Month);
        if (end.Day < start.Day)
        {
            months -= 1;
        }
        return Math.Max(0, months);
    }
}

/// <summary>
/// R1202 — validates <see cref="CapitalisedPaymentRequestModifyInputDto"/>.
/// Each nullable field is validated only when supplied; <c>ChangeReason</c>
/// is always required.
/// </summary>
public sealed class CapitalisedPaymentRequestModifyInputValidator
    : AbstractValidator<CapitalisedPaymentRequestModifyInputDto>
{
    /// <summary>Wires the rule set.</summary>
    /// <param name="clock">UTC clock used to compute the birth-date upper bound.</param>
    public CapitalisedPaymentRequestModifyInputValidator(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var todayUtc = DateOnly.FromDateTime(clock.UtcNow);

        RuleFor(x => x.BeneficiaryBirthDate!.Value)
            .Must(d => d <= todayUtc)
            .WithMessage("BeneficiaryBirthDate cannot be in the future.")
            .When(x => x.BeneficiaryBirthDate.HasValue);

        RuleFor(x => x.BeneficiarySex!)
            .Must(CapitalisedPaymentValidatorShared.IsValidSex)
            .WithMessage("BeneficiarySex must be one of Male, Female.")
            .When(x => x.BeneficiarySex is not null);

        RuleFor(x => x.LiquidatedDebtorName!)
            .MinimumLength(CapitalisedPaymentValidatorShared.NameMinLength)
            .WithMessage($"LiquidatedDebtorName must be at least {CapitalisedPaymentValidatorShared.NameMinLength} characters.")
            .MaximumLength(CapitalisedPaymentValidatorShared.NameMaxLength)
            .WithMessage($"LiquidatedDebtorName cannot exceed {CapitalisedPaymentValidatorShared.NameMaxLength} characters.")
            .When(x => x.LiquidatedDebtorName is not null);

        RuleFor(x => x.ObligationKind!)
            .Must(CapitalisedPaymentValidatorShared.IsValidObligationKind)
            .WithMessage("ObligationKind must be one of IncapacityForWork, LossOfBreadwinner, OccupationalDisease.")
            .When(x => x.ObligationKind is not null);

        RuleFor(x => x.MonthlyAmountMdl!.Value)
            .GreaterThan(0m).WithMessage("MonthlyAmountMdl must be > 0.")
            .LessThanOrEqualTo(CapitalisedPaymentValidatorShared.MaxAmount)
            .WithMessage($"MonthlyAmountMdl cannot exceed {CapitalisedPaymentValidatorShared.MaxAmount:0}.")
            .When(x => x.MonthlyAmountMdl.HasValue);

        RuleFor(x => x.LegalDiscountRatePercent!.Value)
            .GreaterThanOrEqualTo(0m).WithMessage("LegalDiscountRatePercent must be >= 0.")
            .LessThanOrEqualTo(CapitalisedPaymentValidatorShared.MaxRatePercent)
            .WithMessage($"LegalDiscountRatePercent cannot exceed {CapitalisedPaymentValidatorShared.MaxRatePercent}.")
            .When(x => x.LegalDiscountRatePercent.HasValue);

        RuleFor(x => x.ChangeReason)
            .NotEmpty().WithMessage("ChangeReason is required.")
            .MinimumLength(CapitalisedPaymentValidatorShared.ReasonMinLength)
            .WithMessage($"ChangeReason must be at least {CapitalisedPaymentValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(CapitalisedPaymentValidatorShared.ReasonMaxLength)
            .WithMessage($"ChangeReason cannot exceed {CapitalisedPaymentValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R1202 — validates <see cref="CapitalisedPaymentReasonInputDto"/> used by
/// reject + cancel endpoints. Enforces the 3..1000 char reason shape.
/// </summary>
public sealed class CapitalisedPaymentReasonInputValidator
    : AbstractValidator<CapitalisedPaymentReasonInputDto>
{
    /// <summary>Wires the rule set.</summary>
    public CapitalisedPaymentReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(CapitalisedPaymentValidatorShared.ReasonMinLength)
            .WithMessage($"Reason must be at least {CapitalisedPaymentValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(CapitalisedPaymentValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason cannot exceed {CapitalisedPaymentValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R1202 — validates <see cref="CapitalisedPaymentApprovalInputDto"/>.
/// Enforces the 3..1000 char note shape.
/// </summary>
public sealed class CapitalisedPaymentApprovalInputValidator
    : AbstractValidator<CapitalisedPaymentApprovalInputDto>
{
    /// <summary>Wires the rule set.</summary>
    public CapitalisedPaymentApprovalInputValidator()
    {
        RuleFor(x => x.Note)
            .NotEmpty().WithMessage("Note is required.")
            .MinimumLength(CapitalisedPaymentValidatorShared.ReasonMinLength)
            .WithMessage($"Note must be at least {CapitalisedPaymentValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(CapitalisedPaymentValidatorShared.ReasonMaxLength)
            .WithMessage($"Note cannot exceed {CapitalisedPaymentValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R1202 — validates <see cref="CapitalisedPaymentSettlementInputDto"/>.
/// </summary>
public sealed class CapitalisedPaymentSettlementInputValidator
    : AbstractValidator<CapitalisedPaymentSettlementInputDto>
{
    /// <summary>Wires the rule set.</summary>
    public CapitalisedPaymentSettlementInputValidator()
    {
        RuleFor(x => x.TreasuryReceiptSqid)
            .NotEmpty().WithMessage("TreasuryReceiptSqid is required.")
            .MaximumLength(64)
            .WithMessage("TreasuryReceiptSqid cannot exceed 64 characters.");

        RuleFor(x => x.SettlementNote)
            .NotEmpty().WithMessage("SettlementNote is required.")
            .MinimumLength(CapitalisedPaymentValidatorShared.ReasonMinLength)
            .WithMessage($"SettlementNote must be at least {CapitalisedPaymentValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(CapitalisedPaymentValidatorShared.ReasonMaxLength)
            .WithMessage($"SettlementNote cannot exceed {CapitalisedPaymentValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R1202 — validates <see cref="CapitalisedPaymentRequestFilterDto"/>. Enforces
/// the Skip / Take page-window bounds and rejects unknown status / kind names.
/// </summary>
public sealed class CapitalisedPaymentRequestFilterValidator
    : AbstractValidator<CapitalisedPaymentRequestFilterDto>
{
    /// <summary>Maximum permitted page size.</summary>
    public const int MaxTake = 100;

    /// <summary>Wires the rule set.</summary>
    public CapitalisedPaymentRequestFilterValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0).WithMessage("Skip must be >= 0.");

        RuleFor(x => x.Take)
            .InclusiveBetween(1, MaxTake)
            .WithMessage($"Take must be between 1 and {MaxTake}.");

        RuleFor(x => x.Status!)
            .Must(CapitalisedPaymentValidatorShared.IsValidStatus)
            .WithMessage("Status must be a known CapitalisedPaymentRequestStatus enum name.")
            .When(x => !string.IsNullOrEmpty(x.Status));

        RuleFor(x => x.ObligationKind!)
            .Must(CapitalisedPaymentValidatorShared.IsValidObligationKind)
            .WithMessage("ObligationKind must be a known CapitalisedPaymentObligationKind enum name.")
            .When(x => !string.IsNullOrEmpty(x.ObligationKind));
    }
}
