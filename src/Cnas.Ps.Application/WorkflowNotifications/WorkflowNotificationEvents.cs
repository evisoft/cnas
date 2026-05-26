using System.Collections.Generic;

namespace Cnas.Ps.Application.WorkflowNotifications;

/// <summary>
/// R0128 / R0173 — canonical list of workflow lifecycle event codes a strategy may
/// govern. This set is the validator's allow-list AND the production code's vocabulary
/// — every dispatch site uses one of these strings and the CRUD validator rejects
/// strategies for anything outside the set.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a frozen string list rather than an enum.</b> Stable string constants survive
/// serialisation round-trips through Postgres + JSON without coupling the Contracts
/// assembly (which doesn't reference Core) to the Application layer. New events are
/// added by appending here AND wiring a dispatch site — the validator picks up the new
/// value automatically via the static <see cref="All"/> set.
/// </para>
/// <para>
/// <b>Naming convention.</b> Dotted PascalCase pairs — <c>Domain.Action</c>. The first
/// segment is the conceptual entity (Task, Workflow); the second is the lifecycle moment
/// (Assigned, Completed). Backward-compatibility rule: never rename an event after it
/// ships — strategies in the wild reference these strings.
/// </para>
/// </remarks>
public static class WorkflowNotificationEvents
{
    /// <summary>A new workflow task has been assigned to a user or group inbox.</summary>
    public const string TaskAssigned = "Task.Assigned";

    /// <summary>An existing workflow task has been reassigned to a different user or group.</summary>
    public const string TaskReassigned = "Task.Reassigned";

    /// <summary>A workflow task transitioned to <c>Completed</c>.</summary>
    public const string TaskCompleted = "Task.Completed";

    /// <summary>
    /// A workflow task passed its SLA deadline without being completed (R0166 / CF
    /// 20.05 sibling). The orchestrator is invoked by the SLA monitor jobs.
    /// </summary>
    public const string TaskOverdue = "Task.Overdue";

    /// <summary>
    /// A workflow task remained in the unclaimed group-inbox pool past the configured
    /// window and was escalated (R0202 / CF 20.05).
    /// </summary>
    public const string TaskUnclaimedEscalated = "Task.UnclaimedEscalated";

    /// <summary>
    /// A workflow instance has been started (e.g. dossier opened, application accepted
    /// for examination). Fires once per workflow instance.
    /// </summary>
    public const string WorkflowStarted = "Workflow.Started";

    /// <summary>A workflow instance reached a terminal state.</summary>
    public const string WorkflowCompleted = "Workflow.Completed";

    /// <summary>
    /// Frozen allow-list consulted by the validator + the orchestrator's default
    /// template lookup. Built once at type-load; the <see cref="HashSet{T}"/> backing
    /// gives O(1) <c>Contains</c> in the hot validation path.
    /// </summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(System.StringComparer.Ordinal)
    {
        TaskAssigned,
        TaskReassigned,
        TaskCompleted,
        TaskOverdue,
        TaskUnclaimedEscalated,
        WorkflowStarted,
        WorkflowCompleted,
    };
}
