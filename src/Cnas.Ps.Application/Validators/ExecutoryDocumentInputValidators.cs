using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R1600 / R1406 — shared constants + helpers for the executory-document
/// validators. Centralised so the magic numbers do not drift across rule sets.
/// </summary>
internal static class ExecutoryDocumentValidatorShared
{
    /// <summary>Minimum permitted reason / change-reason length.</summary>
    public const int ReasonMinLength = 3;

    /// <summary>Maximum permitted reason / change-reason length.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>Maximum permitted issuer / creditor-name length.</summary>
    public const int NameMaxLength = 256;

    /// <summary>Minimum permitted issuer / creditor-name length.</summary>
    public const int NameMinLength = 3;

    /// <summary>Maximum permitted document-series-number length.</summary>
    public const int SeriesNumberMaxLength = 32;

    /// <summary>Maximum permitted monetary amount (MDL).</summary>
    public const decimal MaxAmount = 100_000_000m;

    /// <summary>Maximum permitted withholding percentage (per art. 156 CMP).</summary>
    public const decimal MaxPercentage = 70m;

    /// <summary>Highest priority rank (lowest number).</summary>
    public const int MinPriorityRank = 1;

    /// <summary>Lowest priority rank (highest number).</summary>
    public const int MaxPriorityRank = 5;

    /// <summary>Compiled IDNP regex — exactly 13 ASCII digits.</summary>
    public static readonly Regex IdnpRegex = new(
        "^[0-9]{13}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// Compiled Moldovan IBAN regex per ISO-13616 BBAN structure for country
    /// code MD: 2 letters + 2 check digits + 20 alphanumeric chars (total 24
    /// chars, UPPERCASE).
    /// </summary>
    public static readonly Regex MdIbanRegex = new(
        "^MD[0-9]{2}[A-Z0-9]{20}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    /// <summary>True when <paramref name="kind"/> parses to a known <see cref="ExecutoryDocumentKind"/> name (case-sensitive).</summary>
    /// <param name="kind">Candidate enum-name string.</param>
    /// <returns>True when the value parses.</returns>
    public static bool IsValidKind(string? kind) =>
        kind is not null && Enum.TryParse<ExecutoryDocumentKind>(kind, ignoreCase: false, out _);

    /// <summary>True when <paramref name="mode"/> parses to a known <see cref="ExecutoryDocumentWithholdingMode"/> name (case-sensitive).</summary>
    /// <param name="mode">Candidate enum-name string.</param>
    /// <returns>True when the value parses.</returns>
    public static bool IsValidMode(string? mode) =>
        mode is not null && Enum.TryParse<ExecutoryDocumentWithholdingMode>(mode, ignoreCase: false, out _);
}

/// <summary>
/// R1600 — validates <see cref="ExecutoryDocumentRegisterInputDto"/>. Enforces
/// the IDNP / IBAN / amount / percentage / priority bounds plus the date-order
/// invariants. Routes through <see cref="ICnasTimeProvider"/> so the
/// IssuedDate ≤ today rule is testable without mutating the system clock.
/// </summary>
public sealed class ExecutoryDocumentRegisterInputValidator : AbstractValidator<ExecutoryDocumentRegisterInputDto>
{
    /// <summary>Wires the rule set against an injected <see cref="ICnasTimeProvider"/>.</summary>
    /// <param name="clock">UTC clock used to compute the IssuedDate ≤ today cut-off.</param>
    public ExecutoryDocumentRegisterInputValidator(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var todayUtc = DateOnly.FromDateTime(clock.UtcNow);

        // Series number is optional but capped at 32 chars when supplied.
        RuleFor(x => x.DocumentSeriesNumber!)
            .MaximumLength(ExecutoryDocumentValidatorShared.SeriesNumberMaxLength)
            .When(x => !string.IsNullOrEmpty(x.DocumentSeriesNumber))
            .WithMessage(
                $"DocumentSeriesNumber cannot exceed {ExecutoryDocumentValidatorShared.SeriesNumberMaxLength} characters.");

        RuleFor(x => x.DebtorIdnp)
            .NotEmpty().WithMessage("DebtorIdnp is required.")
            .Must(idnp => ExecutoryDocumentValidatorShared.IdnpRegex.IsMatch(idnp ?? string.Empty))
            .WithMessage("DebtorIdnp must be exactly 13 digits.");

        RuleFor(x => x.Kind)
            .NotEmpty().WithMessage("Kind is required.")
            .Must(ExecutoryDocumentValidatorShared.IsValidKind)
            .WithMessage("Kind must be one of CourtOrder, BailiffOrder, NotaryOrder, AdministrativeOrder, Other.");

        RuleFor(x => x.IssuedBy)
            .NotEmpty().WithMessage("IssuedBy is required.")
            .MinimumLength(ExecutoryDocumentValidatorShared.NameMinLength)
            .WithMessage($"IssuedBy must be at least {ExecutoryDocumentValidatorShared.NameMinLength} characters.")
            .MaximumLength(ExecutoryDocumentValidatorShared.NameMaxLength)
            .WithMessage($"IssuedBy cannot exceed {ExecutoryDocumentValidatorShared.NameMaxLength} characters.");

        RuleFor(x => x.IssuedDate)
            .Must(d => d <= todayUtc)
            .WithMessage("IssuedDate cannot be in the future.");

        RuleFor(x => x)
            .Must(x => x.EffectiveFrom >= x.IssuedDate)
            .WithMessage("EffectiveFrom must be greater than or equal to IssuedDate.");

        RuleFor(x => x)
            .Must(x => !x.EffectiveUntil.HasValue || x.EffectiveUntil.Value >= x.EffectiveFrom)
            .WithMessage("EffectiveUntil must be greater than or equal to EffectiveFrom.");

        RuleFor(x => x.WithholdingMode)
            .NotEmpty().WithMessage("WithholdingMode is required.")
            .Must(ExecutoryDocumentValidatorShared.IsValidMode)
            .WithMessage("WithholdingMode must be one of FixedAmount, Percentage, FullExcessOverMinimum.");

        RuleFor(x => x.WithholdingAmountMdl!.Value)
            .GreaterThan(0m).WithMessage("WithholdingAmountMdl must be > 0.")
            .LessThanOrEqualTo(ExecutoryDocumentValidatorShared.MaxAmount)
            .WithMessage($"WithholdingAmountMdl cannot exceed {ExecutoryDocumentValidatorShared.MaxAmount:0}.")
            .When(x => x.WithholdingAmountMdl.HasValue);

        RuleFor(x => x.WithholdingPercentage!.Value)
            .GreaterThan(0m).WithMessage("WithholdingPercentage must be > 0.")
            .LessThanOrEqualTo(ExecutoryDocumentValidatorShared.MaxPercentage)
            .WithMessage($"WithholdingPercentage cannot exceed {ExecutoryDocumentValidatorShared.MaxPercentage}.")
            .When(x => x.WithholdingPercentage.HasValue);

        RuleFor(x => x.PriorityRank)
            .InclusiveBetween(
                ExecutoryDocumentValidatorShared.MinPriorityRank,
                ExecutoryDocumentValidatorShared.MaxPriorityRank)
            .WithMessage(
                $"PriorityRank must be between {ExecutoryDocumentValidatorShared.MinPriorityRank} and {ExecutoryDocumentValidatorShared.MaxPriorityRank}.");

        RuleFor(x => x.CreditorAccountIban)
            .NotEmpty().WithMessage("CreditorAccountIban is required.")
            .Must(iban => ExecutoryDocumentValidatorShared.MdIbanRegex.IsMatch(iban ?? string.Empty))
            .WithMessage("CreditorAccountIban must be a canonical 24-char Moldovan IBAN (UPPERCASE).");

        RuleFor(x => x.CreditorName)
            .NotEmpty().WithMessage("CreditorName is required.")
            .MinimumLength(ExecutoryDocumentValidatorShared.NameMinLength)
            .WithMessage($"CreditorName must be at least {ExecutoryDocumentValidatorShared.NameMinLength} characters.")
            .MaximumLength(ExecutoryDocumentValidatorShared.NameMaxLength)
            .WithMessage($"CreditorName cannot exceed {ExecutoryDocumentValidatorShared.NameMaxLength} characters.");

        RuleFor(x => x.TotalOwedMdl!.Value)
            .GreaterThan(0m).WithMessage("TotalOwedMdl must be > 0.")
            .LessThanOrEqualTo(ExecutoryDocumentValidatorShared.MaxAmount)
            .WithMessage($"TotalOwedMdl cannot exceed {ExecutoryDocumentValidatorShared.MaxAmount:0}.")
            .When(x => x.TotalOwedMdl.HasValue);
    }
}

/// <summary>
/// R1600 — validates <see cref="ExecutoryDocumentModifyInputDto"/>. Each
/// nullable field is validated only when supplied; <c>ChangeReason</c> is
/// always required.
/// </summary>
public sealed class ExecutoryDocumentModifyInputValidator : AbstractValidator<ExecutoryDocumentModifyInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public ExecutoryDocumentModifyInputValidator()
    {
        RuleFor(x => x.IssuedBy!)
            .MinimumLength(ExecutoryDocumentValidatorShared.NameMinLength)
            .WithMessage($"IssuedBy must be at least {ExecutoryDocumentValidatorShared.NameMinLength} characters.")
            .MaximumLength(ExecutoryDocumentValidatorShared.NameMaxLength)
            .WithMessage($"IssuedBy cannot exceed {ExecutoryDocumentValidatorShared.NameMaxLength} characters.")
            .When(x => x.IssuedBy is not null);

        RuleFor(x => x.WithholdingMode!)
            .Must(ExecutoryDocumentValidatorShared.IsValidMode)
            .WithMessage("WithholdingMode must be one of FixedAmount, Percentage, FullExcessOverMinimum.")
            .When(x => x.WithholdingMode is not null);

        RuleFor(x => x.WithholdingAmountMdl!.Value)
            .GreaterThan(0m).WithMessage("WithholdingAmountMdl must be > 0.")
            .LessThanOrEqualTo(ExecutoryDocumentValidatorShared.MaxAmount)
            .WithMessage($"WithholdingAmountMdl cannot exceed {ExecutoryDocumentValidatorShared.MaxAmount:0}.")
            .When(x => x.WithholdingAmountMdl.HasValue);

        RuleFor(x => x.WithholdingPercentage!.Value)
            .GreaterThan(0m).WithMessage("WithholdingPercentage must be > 0.")
            .LessThanOrEqualTo(ExecutoryDocumentValidatorShared.MaxPercentage)
            .WithMessage($"WithholdingPercentage cannot exceed {ExecutoryDocumentValidatorShared.MaxPercentage}.")
            .When(x => x.WithholdingPercentage.HasValue);

        RuleFor(x => x.PriorityRank!.Value)
            .InclusiveBetween(
                ExecutoryDocumentValidatorShared.MinPriorityRank,
                ExecutoryDocumentValidatorShared.MaxPriorityRank)
            .WithMessage(
                $"PriorityRank must be between {ExecutoryDocumentValidatorShared.MinPriorityRank} and {ExecutoryDocumentValidatorShared.MaxPriorityRank}.")
            .When(x => x.PriorityRank.HasValue);

        RuleFor(x => x.CreditorAccountIban!)
            .Must(iban => ExecutoryDocumentValidatorShared.MdIbanRegex.IsMatch(iban))
            .WithMessage("CreditorAccountIban must be a canonical 24-char Moldovan IBAN (UPPERCASE).")
            .When(x => x.CreditorAccountIban is not null);

        RuleFor(x => x.CreditorName!)
            .MinimumLength(ExecutoryDocumentValidatorShared.NameMinLength)
            .WithMessage($"CreditorName must be at least {ExecutoryDocumentValidatorShared.NameMinLength} characters.")
            .MaximumLength(ExecutoryDocumentValidatorShared.NameMaxLength)
            .WithMessage($"CreditorName cannot exceed {ExecutoryDocumentValidatorShared.NameMaxLength} characters.")
            .When(x => x.CreditorName is not null);

        RuleFor(x => x.TotalOwedMdl!.Value)
            .GreaterThan(0m).WithMessage("TotalOwedMdl must be > 0.")
            .LessThanOrEqualTo(ExecutoryDocumentValidatorShared.MaxAmount)
            .WithMessage($"TotalOwedMdl cannot exceed {ExecutoryDocumentValidatorShared.MaxAmount:0}.")
            .When(x => x.TotalOwedMdl.HasValue);

        RuleFor(x => x.ChangeReason)
            .NotEmpty().WithMessage("ChangeReason is required.")
            .MinimumLength(ExecutoryDocumentValidatorShared.ReasonMinLength)
            .WithMessage($"ChangeReason must be at least {ExecutoryDocumentValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(ExecutoryDocumentValidatorShared.ReasonMaxLength)
            .WithMessage($"ChangeReason cannot exceed {ExecutoryDocumentValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R1600 — validates <see cref="ExecutoryDocumentReasonInputDto"/> (used by
/// suspend / resume / cancel endpoints). Enforces the standard 3..500 char
/// reason shape.
/// </summary>
public sealed class ExecutoryDocumentReasonInputValidator : AbstractValidator<ExecutoryDocumentReasonInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public ExecutoryDocumentReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(ExecutoryDocumentValidatorShared.ReasonMinLength)
            .WithMessage($"Reason must be at least {ExecutoryDocumentValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(ExecutoryDocumentValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason cannot exceed {ExecutoryDocumentValidatorShared.ReasonMaxLength} characters.");
    }
}
