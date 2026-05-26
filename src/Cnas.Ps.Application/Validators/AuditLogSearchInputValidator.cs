using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0193 / TOR SEC 052 — FluentValidation rules for
/// <see cref="AuditLogSearchInput"/>. Enforces the paging cap (Take ≤ 200, Skip
/// ≥ 0) and the date-range monotonicity (FromUtc ≤ ToUtc when both supplied).
/// </summary>
/// <remarks>
/// The QBE envelope itself is NOT validated here — the converter
/// (<see cref="Cnas.Ps.Application.Qbe.IQbeToLinqConverter"/>) emits stable
/// <c>QBE_*</c> error codes for malformed envelopes and the service surfaces
/// those verbatim. Splitting the validation responsibility keeps the envelope
/// validator focused on the lightweight shape checks the controller can run
/// before opening the DB scope.
/// </remarks>
public sealed class AuditLogSearchInputValidator : AbstractValidator<AuditLogSearchInput>
{
    /// <summary>Hard server-side cap on <see cref="AuditLogSearchInput.Take"/>.</summary>
    public const int MaxTake = 200;

    /// <summary>Creates the validator with the canonical rule set.</summary>
    public AuditLogSearchInputValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Skip must be greater than or equal to 0.");

        RuleFor(x => x.Take)
            .GreaterThan(0).WithMessage("Take must be greater than 0.")
            .LessThanOrEqualTo(MaxTake)
            .WithMessage($"Take must not exceed the server-side cap of {MaxTake}.");

        // Date-range monotonicity — both bounds are optional; only when BOTH are
        // present do we enforce FromUtc <= ToUtc. A single-bounded range is
        // structurally legal (e.g. "all events after 2026-01-01").
        RuleFor(x => x)
            .Must(x => !(x.FromUtc is { } from && x.ToUtc is { } to) || from <= to)
            .WithName(nameof(AuditLogSearchInput.FromUtc))
            .WithMessage("FromUtc must be less than or equal to ToUtc when both are provided.");
    }
}
