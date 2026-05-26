using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Core.ValueObjects;

/// <summary>
/// R0574 / R0592 / TOR CF 08.06 / CF 10.03 — pure-function helper that computes the
/// next-or-previous-level routing verdict for the canonical 3-level CNAS approval
/// chain pinned by <see cref="WorkflowApprovalLevel"/>.
/// </summary>
/// <remarks>
/// <para>
/// The chain is <c>UserCnas → DirectorOfDirectorate → ChiefCnas</c>. For 2-level
/// passports the engine simply skips <see cref="WorkflowApprovalLevel.DirectorOfDirectorate"/>;
/// the helpers below intentionally model the FULL 3-level path so the caller is free
/// to short-circuit per service-passport configuration.
/// </para>
/// <para>
/// Pure synchronous: no I/O, no clock access, no exceptions for business outcomes.
/// </para>
/// </remarks>
public static class WorkflowRouting
{
    /// <summary>
    /// Computes the next level in the approval chain. Returns the same level when the
    /// caller is already at the top of the chain (<see cref="WorkflowApprovalLevel.ChiefCnas"/>),
    /// with <c>IsTerminal=true</c> on the verdict so the caller can treat the call as
    /// a no-op forward.
    /// </summary>
    /// <param name="currentLevel">The level we are forwarding FROM.</param>
    /// <param name="reason">Optional free-text reason.</param>
    /// <returns>
    /// A tuple of (decision, isTerminal). When <c>isTerminal</c> is true the chain is
    /// already at <see cref="WorkflowApprovalLevel.ChiefCnas"/> and the caller should
    /// treat the result as a no-op (no status flip required).
    /// </returns>
    public static (WorkflowRoutingDecision Decision, bool IsTerminal) ComputeNextLevel(
        WorkflowApprovalLevel currentLevel,
        string? reason)
    {
        return currentLevel switch
        {
            WorkflowApprovalLevel.UserCnas =>
                (WorkflowRoutingDecision.Create(WorkflowApprovalLevel.DirectorOfDirectorate, reason ?? "Forwarded to direction head."), false),
            WorkflowApprovalLevel.DirectorOfDirectorate =>
                (WorkflowRoutingDecision.Create(WorkflowApprovalLevel.ChiefCnas, reason ?? "Forwarded to CNAS chief."), false),
            WorkflowApprovalLevel.ChiefCnas =>
                (WorkflowRoutingDecision.Create(WorkflowApprovalLevel.ChiefCnas, reason ?? "Already at top of chain."), true),
            _ => (WorkflowRoutingDecision.Create(currentLevel, reason ?? string.Empty), true),
        };
    }

    /// <summary>
    /// Computes the previous level in the approval chain — used by the
    /// return-to-previous-step branch (R0592 / CF 10.03).
    /// </summary>
    /// <param name="currentLevel">The level we are returning FROM.</param>
    /// <param name="reason">Optional free-text reason.</param>
    /// <returns>
    /// A tuple of (decision, isAtFloor). When <c>isAtFloor</c> is true the chain is
    /// already at <see cref="WorkflowApprovalLevel.UserCnas"/> and the caller MUST
    /// treat the return as a hard rejection (no examiner to bounce back to).
    /// </returns>
    public static (WorkflowRoutingDecision Decision, bool IsAtFloor) ComputePreviousLevel(
        WorkflowApprovalLevel currentLevel,
        string? reason)
    {
        return currentLevel switch
        {
            WorkflowApprovalLevel.ChiefCnas =>
                (WorkflowRoutingDecision.Create(WorkflowApprovalLevel.DirectorOfDirectorate, reason ?? "Returned to direction head."), false),
            WorkflowApprovalLevel.DirectorOfDirectorate =>
                (WorkflowRoutingDecision.Create(WorkflowApprovalLevel.UserCnas, reason ?? "Returned to examiner."), false),
            WorkflowApprovalLevel.UserCnas =>
                (WorkflowRoutingDecision.Create(WorkflowApprovalLevel.UserCnas, reason ?? "Already at bottom of chain."), true),
            _ => (WorkflowRoutingDecision.Create(currentLevel, reason ?? string.Empty), true),
        };
    }

    /// <summary>
    /// Maps a <see cref="WorkflowApprovalLevel"/> to the canonical
    /// <see cref="Domain.ApplicationStatus"/> after a successful sign-off at that level.
    /// </summary>
    /// <param name="level">Approval level to map.</param>
    /// <returns>The matching application status, or <c>null</c> for unmapped levels.</returns>
    public static Domain.ApplicationStatus? ToApplicationStatus(WorkflowApprovalLevel level)
    {
        return level switch
        {
            WorkflowApprovalLevel.UserCnas => Domain.ApplicationStatus.PendingApproval,
            WorkflowApprovalLevel.DirectorOfDirectorate => Domain.ApplicationStatus.SignedByDirector,
            WorkflowApprovalLevel.ChiefCnas => Domain.ApplicationStatus.Approved,
            _ => null,
        };
    }

    /// <summary>
    /// Inverse of <see cref="ToApplicationStatus"/>: maps a current
    /// <see cref="Domain.ApplicationStatus"/> back to the approval level the workflow
    /// is at. Statuses outside the approval chain return <c>null</c>.
    /// </summary>
    /// <param name="status">Current application status.</param>
    /// <returns>The matching approval level, or <c>null</c>.</returns>
    public static WorkflowApprovalLevel? FromApplicationStatus(Domain.ApplicationStatus status)
    {
        return status switch
        {
            Domain.ApplicationStatus.PendingApproval => WorkflowApprovalLevel.UserCnas,
            Domain.ApplicationStatus.SignedByDirector => WorkflowApprovalLevel.DirectorOfDirectorate,
            Domain.ApplicationStatus.Approved => WorkflowApprovalLevel.ChiefCnas,
            _ => null,
        };
    }
}
