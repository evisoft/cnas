using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0671 continuation — FluentValidation rules for <see cref="DocumentsListInput"/>.
/// Enforces the paging cap (Take ≤ 200, Skip ≥ 0) and the date-range monotonicity
/// (FromUtc ≤ ToUtc when both supplied). Mirrors <see cref="AuditLogSearchInputValidator"/>
/// to keep the canonical search-envelope shape consistent across registries.
/// </summary>
/// <remarks>
/// The QBE envelope itself is NOT validated here — the converter
/// (<see cref="Cnas.Ps.Application.Qbe.IQbeToLinqConverter"/>) emits stable
/// <c>QBE_*</c> error codes for malformed envelopes and the service surfaces those
/// verbatim. Splitting the validation responsibility keeps this envelope validator
/// focused on the lightweight shape checks the controller can run before opening the
/// DB scope.
/// </remarks>
public sealed class DocumentsListInputValidator : AbstractValidator<DocumentsListInput>
{
    /// <summary>Hard server-side cap on <see cref="DocumentsListInput.Take"/>.</summary>
    public const int MaxTake = 200;

    /// <summary>Creates the validator with the canonical rule set.</summary>
    public DocumentsListInputValidator()
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
        // structurally legal.
        RuleFor(x => x)
            .Must(x => !(x.FromUtc is { } from && x.ToUtc is { } to) || from <= to)
            .WithName(nameof(DocumentsListInput.FromUtc))
            .WithMessage("FromUtc must be less than or equal to ToUtc when both are provided.");
    }
}
