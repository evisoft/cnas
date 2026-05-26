namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Sarcină — workflow task assigned to a user or group within a Dossier's lifecycle. TOR §2.3 #9, UC05.
/// </summary>
public sealed class WorkflowTask : AuditableEntity, IExternalId
{
    /// <summary>FK to the Dossier this task is attached to.</summary>
    public long DossierId { get; set; }

    /// <summary>Display title of the task (translated at the presentation layer).</summary>
    public required string Title { get; set; }

    /// <summary>Lifecycle state.</summary>
    public WorkflowTaskStatus Status { get; set; } = WorkflowTaskStatus.Pending;

    /// <summary>FK to the user currently assigned (nullable — could be a group inbox).</summary>
    public long? AssignedUserId { get; set; }

    /// <summary>Group code the task belongs to (for group inboxes).</summary>
    public string? GroupCode { get; set; }

    /// <summary>
    /// UTC timestamp at which the task entered the "unclaimed pool" — i.e. its lifecycle
    /// reached <see cref="WorkflowTaskStatus.Pending"/> with <see cref="AssignedUserId"/>
    /// null AND <see cref="GroupCode"/> populated. The value is null whenever the task is
    /// claimed (<see cref="AssignedUserId"/> non-null) or has moved out of
    /// <see cref="WorkflowTaskStatus.Pending"/>. Drives the unclaimed-task escalation SLA
    /// (R0202 / CF 20.05): the <c>UnclaimedTaskEscalationJob</c> finds rows whose stamp
    /// has elapsed past a configurable window and emits an audit + counter signal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Writer-site invariant.</b> Every site that creates a new task with
    /// <c>GroupCode != null &amp;&amp; AssignedUserId == null</c> MUST stamp this column to
    /// <c>ICnasTimeProvider.UtcNow</c>. Every site that claims a task (sets
    /// <see cref="AssignedUserId"/> to a non-null value) MUST clear this column. Sites
    /// that release a task back to the group inbox MUST re-stamp it. The invariant is
    /// enforced by code review — there is no DB-level trigger.
    /// </para>
    /// <para>
    /// <b>Idempotency anchor.</b> When the escalation job acts on a row it nulls this
    /// stamp. The row therefore drops out of the job's predicate, so a follow-up fire of
    /// the same job does not double-escalate. A row only re-enters the escalation window
    /// if a future writer puts it back into the unclaimed pool (claim → release).
    /// </para>
    /// </remarks>
    public DateTime? UnclaimedSinceUtc { get; set; }

    /// <summary>SLA — UTC deadline by which the task should be completed.</summary>
    public DateTime? DueAtUtc { get; set; }

    /// <summary>UTC timestamp the task was completed (or cancelled).</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// R0127 / CF 16.11 — captures the FIRST owner of the task when it is reassigned via
    /// <c>ITaskInboxService.ReassignAsync</c> or routed through a <c>UserAbsence</c>.
    /// <c>null</c> until the first reassignment; once populated, the value never changes
    /// (subsequent reassignments do NOT overwrite it), so the original owner can always
    /// be restored via <c>RevertReassignmentAsync</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Writer-site invariant.</b> Every reassignment must check
    /// <c>OriginalAssigneeUserId is null</c> and copy the CURRENT
    /// <see cref="AssignedUserId"/> into it BEFORE flipping the latter to the delegate.
    /// This preserves the audit-trail anchor: even after N hops, the row remembers who
    /// first carried it.
    /// </para>
    /// </remarks>
    public long? OriginalAssigneeUserId { get; set; }

    /// <summary>
    /// R0127 / CF 16.11 — FK to the <c>UserAbsence</c> that routed this task to the
    /// delegate. <c>null</c> when the task was reassigned per-task (not through an
    /// absence) or has never been reassigned. Cleared on <c>RevertReassignmentAsync</c>
    /// to break the link so a completed absence's revert sweep does not double-revert
    /// the same row.
    /// </summary>
    public long? DelegatedFromAbsenceId { get; set; }

    /// <summary>
    /// R0127 / CF 16.11 — free-text reason supplied at the time of the most recent
    /// reassignment (e.g. <c>"Concediu medical"</c>, <c>"Examinator a refuzat"</c>).
    /// Mutable: every reassignment overwrites this column with the new reason; the
    /// historical reasons live on the audit-log rows emitted at each transition.
    /// </summary>
    public string? ReassignmentReason { get; set; }

    /// <summary>
    /// R0127 / CF 16.11 — monotonically-increasing counter of reassignments since the
    /// row was created. Defaults to <c>0</c> and is incremented by every reassignment
    /// (per-task or absence-driven) and every revert. Used by audit dashboards to
    /// correlate <c>WORKFLOWTASK.REASSIGNED</c> audit-log rows back to a stable
    /// per-task sequence number.
    /// </summary>
    public int ReassignmentCount { get; set; }

    /// <summary>
    /// R0123 / TOR CF 16.05 — stable code of the <see cref="WorkflowGraphNode"/> this
    /// task is currently anchored to. Populated on every task created by the
    /// <c>WorkflowGraphExecutor</c>; <c>null</c> on legacy tasks that pre-date the
    /// graph executor (the executor's "advance" path tolerates null by returning the
    /// no-op success — the task is simply not part of a known graph).
    /// </summary>
    /// <remarks>
    /// <b>Why a string, not an FK.</b> Workflow graphs are version-pinned per R0129;
    /// the surrogate id of the <see cref="WorkflowGraphNode"/> changes across versions
    /// even when the node's CODE stays stable. Storing the code (not the id) keeps the
    /// task readable across version transitions and matches the executor's lookup
    /// semantics — it always resolves edges by code on the workflow currently pinned
    /// to the task.
    /// </remarks>
    public string? NodeCode { get; set; }

    /// <summary>
    /// R0123 / TOR CF 16.05 — when this task was spawned by a
    /// <see cref="WorkflowNodeKind.AndSplit"/> fan-out, points back to the synthetic
    /// parent split-task row so the matching <see cref="WorkflowNodeKind.AndJoin"/> can
    /// detect when every sibling has completed. <c>null</c> on tasks that were not
    /// spawned by an AND-split (sequential / OR-split / initial / legacy).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Synthetic parent.</b> The executor creates one "split anchor" task per
    /// AND-split traversal (kind = <see cref="WorkflowNodeKind.AndSplit"/>); the
    /// anchor itself completes immediately, but its id lives on so the AND-join can
    /// query <c>WorkflowTasks.Where(t =&gt; t.ParentSplitTaskId == anchorId)</c> to
    /// count siblings.
    /// </para>
    /// </remarks>
    public long? ParentSplitTaskId { get; set; }
}
