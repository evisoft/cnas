using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0830 / TOR Annex 1 §8.1.4.5 — validates <see cref="InsolvencyOpenInputDto"/>
/// before the lifecycle service opens a case. Enforces:
/// <list type="bullet">
///   <item><description><c>ContributorSqid</c> non-empty.</description></item>
///   <item><description><c>Reason</c> 3..500 chars.</description></item>
///   <item><description><c>InsolvencyDate</c> not in the future relative to the calling moment.</description></item>
/// </list>
/// The "contributor exists" check is enforced in the service because it requires
/// a DB lookup.
/// </summary>
public sealed class InsolvencyOpenInputValidator : AbstractValidator<InsolvencyOpenInputDto>
{
    /// <summary>
    /// Constructs the validator. The <paramref name="todayUtc"/> reference date is
    /// captured at construction time so a long-lived instance does not drift —
    /// tests pass a deterministic anchor; production callers pass the clock's
    /// <c>UtcNow.Date</c>.
    /// </summary>
    /// <param name="todayUtc">Reference "today" date (UTC) used by the future-date guard.</param>
    public InsolvencyOpenInputValidator(DateOnly todayUtc)
    {
        // Contributor sqid: required + non-empty. The service decodes and rejects
        // unknown ids with NotFound; the validator only guards against blank input.
        RuleFor(x => x.ContributorSqid)
            .NotEmpty().WithMessage("ContributorSqid is required.");

        // Reason: required, 3..500 chars (mirrors the EF column cap).
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be 3 characters or more.")
            .MaximumLength(500).WithMessage("Reason must be 500 characters or fewer.");

        // InsolvencyDate: not in the future. Equal to today is allowed (an operator
        // recording a same-day court ruling).
        RuleFor(x => x.InsolvencyDate)
            .Must(d => d <= todayUtc)
            .WithMessage("InsolvencyDate must not be in the future.");
    }

    /// <summary>
    /// Convenience parameterless constructor — anchors "today" at <see cref="DateOnly.MaxValue"/>
    /// so the future-date guard is effectively disabled. The SERVICE path always uses the
    /// explicit clock-anchored overload; this overload exists ONLY for the controller-side
    /// FluentValidation auto-wiring where the clock isn't injected and the service will
    /// re-validate with the deterministic anchor.
    /// </summary>
    public InsolvencyOpenInputValidator()
        : this(DateOnly.MaxValue)
    {
    }
}

/// <summary>
/// R0830 / TOR Annex 1 §8.1.4.5 — validates <see cref="InsolvencyResolveInputDto"/>.
/// Enforces a 3..500 char resolution rationale (mirrors the EF column cap).
/// </summary>
public sealed class InsolvencyResolveInputValidator : AbstractValidator<InsolvencyResolveInputDto>
{
    /// <summary>Constructs the validator with the resolution-length rule wired in.</summary>
    public InsolvencyResolveInputValidator()
    {
        RuleFor(x => x.Resolution)
            .NotEmpty().WithMessage("Resolution is required.")
            .MinimumLength(3).WithMessage("Resolution must be 3 characters or more.")
            .MaximumLength(500).WithMessage("Resolution must be 500 characters or fewer.");
    }
}

/// <summary>
/// R0834 / TOR Annex 1 §8.1.4.5 — validates <see cref="InsolvencyClaimInputDto"/>.
/// </summary>
public sealed class InsolvencyClaimInputValidator : AbstractValidator<InsolvencyClaimInputDto>
{
    /// <summary>Maximum absolute amount (MDL) we permit on a single claim row.</summary>
    private const decimal AmountCap = 100_000_000m;

    /// <summary>Constructs the validator with field bounds wired in.</summary>
    /// <param name="todayUtc">Reference "today" date for the no-future-date guard on <c>IncurredOn</c>.</param>
    public InsolvencyClaimInputValidator(DateOnly todayUtc)
    {
        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("Amount must be > 0.")
            .LessThanOrEqualTo(AmountCap).WithMessage("Amount exceeds the per-row cap.");

        RuleFor(x => x.Currency)
            .NotEmpty().WithMessage("Currency is required.")
            .Length(3).WithMessage("Currency must be a 3-letter ISO-4217 code.")
            .Matches("^[A-Z]{3}$").WithMessage("Currency must be uppercase ISO-4217.");

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Description is required.")
            .MinimumLength(3).WithMessage("Description must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("Description must be 1000 characters or fewer.");

        RuleFor(x => x.IncurredOn)
            .Must(d => d <= todayUtc)
            .WithMessage("IncurredOn must not be in the future.");
    }

    /// <summary>Convenience parameterless ctor — disables the future-date guard (service re-validates).</summary>
    public InsolvencyClaimInputValidator()
        : this(DateOnly.MaxValue)
    {
    }
}

/// <summary>
/// R0834 / TOR Annex 1 §8.1.4.5 — validates <see cref="InsolvencyPaymentInputDto"/>.
/// </summary>
public sealed class InsolvencyPaymentInputValidator : AbstractValidator<InsolvencyPaymentInputDto>
{
    /// <summary>Maximum absolute amount (MDL) we permit on a single payment row.</summary>
    private const decimal AmountCap = 100_000_000m;

    /// <summary>Constructs the validator with field bounds wired in.</summary>
    /// <param name="todayUtc">Reference "today" date for the no-future-date guard on <c>PaymentDate</c>.</param>
    public InsolvencyPaymentInputValidator(DateOnly todayUtc)
    {
        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("Amount must be > 0.")
            .LessThanOrEqualTo(AmountCap).WithMessage("Amount exceeds the per-row cap.");

        RuleFor(x => x.PaymentDate)
            .Must(d => d <= todayUtc)
            .WithMessage("PaymentDate must not be in the future.");

        RuleFor(x => x.Reference)
            .MaximumLength(64).WithMessage("Reference must be 64 characters or fewer.")
            .When(x => x.Reference is not null);
    }

    /// <summary>Convenience parameterless ctor — disables the future-date guard (service re-validates).</summary>
    public InsolvencyPaymentInputValidator()
        : this(DateOnly.MaxValue)
    {
    }
}
