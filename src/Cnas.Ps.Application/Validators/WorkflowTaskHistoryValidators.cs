using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0125 / CF 16.09 — validator for <see cref="WorkflowTaskHistoryFilterDto"/>. The
/// filter is optional in every field; this validator only bounds the paging numbers
/// so an admin cannot pull tens of thousands of history rows in one request.
/// </summary>
public sealed class WorkflowTaskHistoryFilterDtoValidator
    : AbstractValidator<WorkflowTaskHistoryFilterDto>
{
    /// <summary>Maximum allowed page size.</summary>
    public const int MaxTake = 200;

    /// <summary>Wires the rule set.</summary>
    public WorkflowTaskHistoryFilterDtoValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0)
            .When(x => x.Skip.HasValue)
            .WithMessage("Skip must be ≥ 0.");

        RuleFor(x => x.Take)
            .InclusiveBetween(1, MaxTake)
            .When(x => x.Take.HasValue)
            .WithMessage($"Take must be 1..{MaxTake}.");

        // EventKind must (when present) be a name on the WorkflowTaskStepEventKind enum.
        // We compare by name (case-sensitive) so e.g. "entered" is rejected — the contract
        // is to pass the enum's exact spelling.
        RuleFor(x => x.EventKind)
            .Must(k => k is null || s_validKinds.Contains(k))
            .WithMessage(
                $"EventKind must be one of: {string.Join(", ", s_validKinds)}.");
    }

    /// <summary>
    /// Allow-list of valid <c>EventKind</c> filter values, mirroring the enum names on
    /// <c>WorkflowTaskStepEventKind</c>. Kept in this validator (rather than reaching
    /// through to Core) so the boundary contract is self-contained.
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<string> s_validKinds =
        new(System.StringComparer.Ordinal)
        {
            "Entered",
            "Exited",
            "Reassigned",
            "SlaBreached",
            "Completed",
            "Cancelled",
        };
}
