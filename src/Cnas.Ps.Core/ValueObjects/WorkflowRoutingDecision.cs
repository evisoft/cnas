using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Core.ValueObjects;

/// <summary>
/// R0574 / R0592 / TOR CF 08.06 / CF 10.03 — immutable value object describing the
/// outcome of a CNAS multi-level workflow routing decision. Returned by
/// <see cref="WorkflowRouting.ComputeNextLevel"/> and
/// <see cref="WorkflowRouting.ComputePreviousLevel"/> so callers (services + jobs +
/// audit emitters) can reason about the routing verdict without re-implementing the
/// branching ladder.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a value object.</b> Multi-level forwarding (CF 08.06 acceptance path) and
/// return-to-previous-step (CF 10.03 rejection path) are independent of the BPM engine
/// (Operaton, R0120) — they ride entirely on the iter-137
/// <see cref="WorkflowApprovalLevel"/> + the iter-119 status-transition table. By
/// pinning the verdict as a value object we keep the in-process scaffold trivially
/// unit-testable and let the BPM engine simply observe the resulting transitions.
/// </para>
/// <para>
/// <b>Reason field.</b> Free-text reason carried verbatim into the audit row payload.
/// Capped at 1000 characters to mirror the rest of the workflow-audit notes. May be
/// empty (the routing helpers default to a deterministic reason string when not
/// supplied).
/// </para>
/// </remarks>
public sealed class WorkflowRoutingDecision
{
    /// <summary>Maximum length of the free-text reason captured on the audit row.</summary>
    public const int MaxReasonLength = 1000;

    private WorkflowRoutingDecision(WorkflowApprovalLevel nextLevel, string reason)
    {
        NextLevel = nextLevel;
        Reason = reason;
    }

    /// <summary>
    /// The level the workflow advances (or returns) to as a result of this routing
    /// decision. Maps onto the application-status lifecycle as documented on
    /// <see cref="WorkflowApprovalLevel"/>.
    /// </summary>
    public WorkflowApprovalLevel NextLevel { get; }

    /// <summary>Operator-supplied reason carried verbatim into the audit payload.</summary>
    public string Reason { get; }

    /// <summary>
    /// Factory — clamps the reason to <see cref="MaxReasonLength"/> characters and
    /// substitutes a deterministic default when the supplied reason is null or
    /// whitespace. Never returns null.
    /// </summary>
    /// <param name="nextLevel">Target level for this routing verdict.</param>
    /// <param name="reason">
    /// Optional free-text reason. Whitespace-only is treated as empty; values longer
    /// than <see cref="MaxReasonLength"/> are truncated (a defensive belt-and-braces
    /// guard — callers SHOULD pre-validate via FluentValidation at the API boundary).
    /// </param>
    /// <returns>The immutable value object.</returns>
    public static WorkflowRoutingDecision Create(WorkflowApprovalLevel nextLevel, string? reason)
    {
        var clean = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason!.Trim();
        if (clean.Length > MaxReasonLength)
        {
            clean = clean[..MaxReasonLength];
        }
        return new WorkflowRoutingDecision(nextLevel, clean);
    }
}
