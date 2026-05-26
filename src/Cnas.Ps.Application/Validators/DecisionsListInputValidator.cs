using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0671 continuation — FluentValidation rules for <see cref="DecisionsListInput"/>.
/// Enforces the paging cap (Take ≤ 200, Skip ≥ 0) and the date-range monotonicity
/// (FromUtc ≤ ToUtc when both supplied). Mirrors
/// <see cref="DocumentsListInputValidator"/>.
/// </summary>
/// <remarks>
/// QBE envelope validation rides through the converter (stable <c>QBE_*</c> codes); this
/// validator is the lightweight shape check the controller can run before opening the
/// DB scope.
/// </remarks>
public sealed class DecisionsListInputValidator : AbstractValidator<DecisionsListInput>
{
    /// <summary>Hard server-side cap on <see cref="DecisionsListInput.Take"/>.</summary>
    public const int MaxTake = 200;

    /// <summary>Creates the validator with the canonical rule set.</summary>
    public DecisionsListInputValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Skip must be greater than or equal to 0.");

        RuleFor(x => x.Take)
            .GreaterThan(0).WithMessage("Take must be greater than 0.")
            .LessThanOrEqualTo(MaxTake)
            .WithMessage($"Take must not exceed the server-side cap of {MaxTake}.");

        RuleFor(x => x)
            .Must(x => !(x.FromUtc is { } from && x.ToUtc is { } to) || from <= to)
            .WithName(nameof(DecisionsListInput.FromUtc))
            .WithMessage("FromUtc must be less than or equal to ToUtc when both are provided.");
    }
}
