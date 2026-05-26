using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R2274 / TOR SEC 028 — validator for <see cref="AccessRightsReportPagingDto"/>.
/// Enforces Skip ≥ 0 and Take 1..500. The 500-row cap mirrors the bulk-action
/// quota size and keeps the by-role and full-matrix CSV payloads bounded.
/// </summary>
public sealed class AccessRightsReportPagingValidator : AbstractValidator<AccessRightsReportPagingDto>
{
    /// <summary>Maximum permitted page size for the access-rights paged report.</summary>
    public const int MaxTake = 500;

    /// <summary>Builds the rule set.</summary>
    public AccessRightsReportPagingValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Skip must be >= 0.");

        RuleFor(x => x.Take)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Take must be >= 1.")
            .LessThanOrEqualTo(MaxTake)
            .WithMessage($"Take cannot exceed {MaxTake}.");
    }
}
