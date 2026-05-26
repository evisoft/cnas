using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R1505 / TOR §3.7-F — FluentValidation rules for the body of
/// <c>POST /api/decisions/recovery/{sqid}/recovered</c>. Enforces the strictly
/// positive recovered-amount invariant and a sanity cap on the upper bound so
/// a typo cannot record a 10^12 MDL "recovery" against a decision.
/// </summary>
/// <remarks>
/// The validator is wired into DI via the
/// <c>AddValidatorsFromAssemblyContaining&lt;ApplicationAssemblyMarker&gt;</c>
/// call in <c>ApplicationServiceCollectionExtensions</c>; the controller
/// injects it and invokes it before forwarding to the recovery service.
/// </remarks>
public sealed class RecoveryRecordedInputValidator : AbstractValidator<RecoveryRecordedInputDto>
{
    /// <summary>Upper sanity cap on a single recovered-amount payment (MDL).</summary>
    /// <remarks>
    /// The cap is intentionally generous (100 million MDL) — it exists to catch
    /// typo'd payments rather than to enforce a business policy ceiling. Actual
    /// per-decision balances are enforced downstream by the recovery service.
    /// </remarks>
    public const decimal MaxRecoveredAmount = 100_000_000m;

    /// <summary>Constructs the validator with the documented rule set.</summary>
    public RecoveryRecordedInputValidator()
    {
        RuleFor(x => x.RecoveredAmount)
            .GreaterThan(0m)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("RecoveredAmount must be strictly positive.")
            .LessThanOrEqualTo(MaxRecoveredAmount)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage($"RecoveredAmount must be {MaxRecoveredAmount:N0} MDL or less.");
    }
}
