using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0831 / R0832 — shared constants + helpers for the claims / claim-payments
/// validators. Centralised so the magic numbers don't drift across rule sets.
/// </summary>
internal static class ClaimValidatorShared
{
    /// <summary>Minimum permitted reason / change-reason length.</summary>
    public const int ReasonMinLength = 3;

    /// <summary>Maximum permitted reason / change-reason length.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>Maximum permitted payment-reference length.</summary>
    public const int PaymentReferenceMaxLength = 64;

    /// <summary>Maximum permitted notes length on a payment row.</summary>
    public const int NotesMaxLength = 1000;

    /// <summary>Maximum permitted related-document-reference length on a claim row.</summary>
    public const int RelatedDocumentReferenceMaxLength = 256;

    /// <summary>Maximum permitted monetary amount (MDL) on a claim or payment.</summary>
    public const decimal MaxAmount = 100_000_000m;

    /// <summary>Asserts the supplied month carries <c>Day == 1</c>.</summary>
    /// <param name="month">Candidate month.</param>
    /// <returns><c>true</c> when the day component is 1.</returns>
    public static bool IsFirstOfMonth(DateOnly month) => month.Day == 1;

    /// <summary>
    /// Asserts the supplied <paramref name="kind"/> string parses to a valid
    /// <see cref="ClaimKind"/> enum name (case-sensitive).
    /// </summary>
    /// <param name="kind">Candidate kind name.</param>
    /// <returns><c>true</c> when the value parses.</returns>
    public static bool IsValidClaimKind(string? kind)
        => kind is not null && Enum.TryParse<ClaimKind>(kind, ignoreCase: false, out _);
}

/// <summary>
/// R0831 / BP 1.3-B — validates <see cref="ClaimRegisterInputDto"/>. Enforces
/// the payer Sqid shape, the kind / month / amount bounds, and the
/// due-date-after-opened-date invariant.
/// </summary>
public sealed class ClaimRegisterInputDtoValidator : AbstractValidator<ClaimRegisterInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public ClaimRegisterInputDtoValidator()
    {
        RuleFor(x => x.ContributorSqid)
            .NotEmpty().WithMessage("ContributorSqid is required.");

        RuleFor(x => x.Kind)
            .NotEmpty().WithMessage("Kind is required.")
            .Must(ClaimValidatorShared.IsValidClaimKind)
            .WithMessage("Kind must be one of Contribution, LatePenalty, AdminFine, Court, Other.");

        RuleFor(x => x.RelatedMonth)
            .Must(ClaimValidatorShared.IsFirstOfMonth)
            .WithMessage("RelatedMonth must be the first day of the month (Day == 1).");

        RuleFor(x => x.PrincipalAmount)
            .GreaterThan(0m).WithMessage("PrincipalAmount must be > 0.")
            .LessThanOrEqualTo(ClaimValidatorShared.MaxAmount)
            .WithMessage($"PrincipalAmount cannot exceed {ClaimValidatorShared.MaxAmount:0}.");

        RuleFor(x => x)
            .Must(x => !x.DueDate.HasValue || x.DueDate.Value >= x.OpenedDate)
            .WithMessage("DueDate must be greater than or equal to OpenedDate.");

        RuleFor(x => x.RelatedDocumentReference!)
            .MaximumLength(ClaimValidatorShared.RelatedDocumentReferenceMaxLength)
            .When(x => x.RelatedDocumentReference is not null)
            .WithMessage(
                $"RelatedDocumentReference cannot exceed {ClaimValidatorShared.RelatedDocumentReferenceMaxLength} characters.");
    }
}

/// <summary>
/// R0831 / BP 1.3-B — validates <see cref="ClaimModifyInputDto"/>. Enforces
/// the optional principal amount bounds, the optional reference length, and
/// the mandatory change-reason shape.
/// </summary>
public sealed class ClaimModifyInputDtoValidator : AbstractValidator<ClaimModifyInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public ClaimModifyInputDtoValidator()
    {
        RuleFor(x => x.PrincipalAmount!.Value)
            .GreaterThan(0m).WithMessage("PrincipalAmount must be > 0.")
            .LessThanOrEqualTo(ClaimValidatorShared.MaxAmount)
            .When(x => x.PrincipalAmount.HasValue)
            .WithMessage($"PrincipalAmount cannot exceed {ClaimValidatorShared.MaxAmount:0}.");

        RuleFor(x => x.RelatedDocumentReference!)
            .MaximumLength(ClaimValidatorShared.RelatedDocumentReferenceMaxLength)
            .When(x => x.RelatedDocumentReference is not null)
            .WithMessage(
                $"RelatedDocumentReference cannot exceed {ClaimValidatorShared.RelatedDocumentReferenceMaxLength} characters.");

        RuleFor(x => x.ChangeReason)
            .NotEmpty().WithMessage("ChangeReason is required.")
            .MinimumLength(ClaimValidatorShared.ReasonMinLength)
            .WithMessage($"ChangeReason must be at least {ClaimValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(ClaimValidatorShared.ReasonMaxLength)
            .WithMessage($"ChangeReason cannot exceed {ClaimValidatorShared.ReasonMaxLength} characters.");
    }
}

/// <summary>
/// R0832 / BP 1.3-C — validates <see cref="ClaimPaymentInputDto"/>. Enforces
/// the amount bounds, the not-in-the-future rule on <c>PaidDate</c>, and the
/// optional reference / notes lengths. The not-in-the-future rule routes
/// through the injected <see cref="ICnasTimeProvider"/> per CLAUDE.md
/// UTC-everywhere principle.
/// </summary>
public sealed class ClaimPaymentInputDtoValidator : AbstractValidator<ClaimPaymentInputDto>
{
    /// <summary>Wires the rule set against an injected <see cref="ICnasTimeProvider"/>.</summary>
    /// <param name="clock">UTC clock used to compute the not-in-the-future cutoff.</param>
    public ClaimPaymentInputDtoValidator(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var todayUtc = DateOnly.FromDateTime(clock.UtcNow);

        RuleFor(x => x.Amount)
            .GreaterThan(0m).WithMessage("Amount must be > 0.")
            .LessThanOrEqualTo(ClaimValidatorShared.MaxAmount)
            .WithMessage($"Amount cannot exceed {ClaimValidatorShared.MaxAmount:0}.");

        RuleFor(x => x.PaidDate)
            .Must(d => d <= todayUtc)
            .WithMessage("PaidDate cannot be in the future.");

        RuleFor(x => x.PaymentReference!)
            .MaximumLength(ClaimValidatorShared.PaymentReferenceMaxLength)
            .When(x => x.PaymentReference is not null)
            .WithMessage(
                $"PaymentReference cannot exceed {ClaimValidatorShared.PaymentReferenceMaxLength} characters.");

        RuleFor(x => x.Notes!)
            .MaximumLength(ClaimValidatorShared.NotesMaxLength)
            .When(x => x.Notes is not null)
            .WithMessage($"Notes cannot exceed {ClaimValidatorShared.NotesMaxLength} characters.");
    }
}

/// <summary>
/// R0831 / BP 1.3-B — validates <see cref="ClaimReasonInputDto"/> (used by the
/// cancel / dispute endpoints). Enforces the standard 3..500 char reason
/// shape.
/// </summary>
public sealed class ClaimReasonInputDtoValidator : AbstractValidator<ClaimReasonInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public ClaimReasonInputDtoValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(ClaimValidatorShared.ReasonMinLength)
            .WithMessage($"Reason must be at least {ClaimValidatorShared.ReasonMinLength} characters.")
            .MaximumLength(ClaimValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason cannot exceed {ClaimValidatorShared.ReasonMaxLength} characters.");
    }
}
