using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0517 / TOR CF 02.05 — validates <see cref="BenefitPaymentStatusQueryDto"/>
/// at the API boundary per CLAUDE.md §2.5. Both bounds are optional; when
/// both are supplied the validator enforces <c>FromMonth ≤ ToMonth</c> and
/// the total window must not exceed <see cref="MaxWindowMonths"/> calendar
/// months. The optional <c>Type</c> filter must parse to a known
/// <see cref="BenefitType"/> enum name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Window cap rationale.</b> The status endpoint is authenticated but the
/// per-call cap protects against accidental "give me everything" requests
/// from a misbehaving client. 36 months covers every plausible review window
/// (annual reconciliation, three-year disability review cycles) while keeping
/// the in-memory aggregation cost bounded.
/// </para>
/// <para>
/// <b>Type parsing.</b> The validator parses the supplied <c>Type</c> string
/// case-sensitively against the enum names — a strict match preserves the
/// stable wire contract documented on <see cref="BenefitType"/>.
/// </para>
/// </remarks>
public sealed class BenefitPaymentStatusQueryDtoValidator : AbstractValidator<BenefitPaymentStatusQueryDto>
{
    /// <summary>Maximum permitted total window size in months (inclusive).</summary>
    public const int MaxWindowMonths = 36;

    /// <summary>Creates the validator with all field rules in place.</summary>
    public BenefitPaymentStatusQueryDtoValidator()
    {
        // Both bounds optional. When both supplied, FromMonth must not be
        // after ToMonth.
        When(x => x.FromMonth.HasValue && x.ToMonth.HasValue, () =>
        {
            RuleFor(x => x)
                .Must(q => q.FromMonth!.Value <= q.ToMonth!.Value)
                .WithMessage("FromMonth must be on or before ToMonth.");

            RuleFor(x => x)
                .Must(q => ComputeMonthsInclusive(q.FromMonth!.Value, q.ToMonth!.Value) <= MaxWindowMonths)
                .WithMessage($"Window must not exceed {MaxWindowMonths} months.");
        });

        // Type filter is optional; when supplied it must be a known benefit
        // type. Strict enum-name match keeps the wire contract stable.
        When(x => !string.IsNullOrEmpty(x.Type), () =>
        {
            RuleFor(x => x.Type!)
                .Must(t => Enum.TryParse<BenefitType>(t, ignoreCase: false, out _))
                .WithMessage("Type must be a valid BenefitType (e.g. OldAgePension).");
        });
    }

    /// <summary>
    /// Counts the inclusive number of calendar months covered by the supplied
    /// range. <c>(2025-01, 2025-01)</c> returns 1; <c>(2025-01, 2025-12)</c>
    /// returns 12; <c>(2024-01, 2026-12)</c> returns 36. The helper is
    /// internal-static so the validator and the service can share the
    /// arithmetic without duplicating it.
    /// </summary>
    /// <param name="from">Inclusive lower bound (any day component permitted; only the year/month is used).</param>
    /// <param name="to">Inclusive upper bound (any day component permitted; only the year/month is used).</param>
    /// <returns>Count of inclusive calendar months covered by <c>[from, to]</c>.</returns>
    public static int ComputeMonthsInclusive(DateOnly from, DateOnly to)
        => ((to.Year - from.Year) * 12) + (to.Month - from.Month) + 1;
}
