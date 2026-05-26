namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0125 / CF 16.09 — append-only projection capturing one transition event in a
/// <see cref="WorkflowTask"/>'s lifecycle. Rows are written by the workflow engine
/// (or the task-inbox service) every time the task changes state, gets reassigned,
/// or breaches its SLA. They support the "traversed steps" history view referenced
/// by TOR CF 16.09 and unlock chronological dashboards over a task's journey.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only.</b> Every row represents a point-in-time event; existing rows are
/// never mutated. <see cref="AuditableEntity.UpdatedAtUtc"/> remains null for the
/// lifetime of the projection.
/// </para>
/// <para>
/// <b>Step code, not FK.</b> <see cref="StepCode"/> is the stable string code of the
/// <see cref="WorkflowGraphNode"/> the task is in / transitioning into. We store the
/// code rather than the surrogate id for the same reason
/// <see cref="WorkflowTask.NodeCode"/> stores a string — workflow graphs are
/// version-pinned per R0129, the node row's <c>Id</c> changes across versions even
/// when the node's code stays stable. The string is the audit-friendly anchor.
/// </para>
/// <para>
/// <b>Actor.</b> <see cref="ActorUserId"/> is null for system events (e.g. an SLA
/// breach detected by the unclaimed-task escalation job). When the actor is a human
/// operator the column carries the internal <c>UserProfile.Id</c> — the projection
/// is internal, so the raw long is fine. External-facing DTOs Sqid-encode the value.
/// </para>
/// </remarks>
public sealed class WorkflowTaskStepHistory : AuditableEntity, IExternalId
{
    /// <summary>FK to the <see cref="WorkflowTask"/> this projection row belongs to.</summary>
    public long WorkflowTaskId { get; set; }

    /// <summary>
    /// Stable code of the step (the <see cref="WorkflowGraphNode"/>) the task is in or
    /// transitioning into. Required, ≤ 64 chars. Lower-case, kebab-friendly form chosen
    /// by the workflow designer — never a Sqid.
    /// </summary>
    public required string StepCode { get; set; }

    /// <summary>
    /// What kind of lifecycle transition this row represents.
    /// </summary>
    public WorkflowTaskStepEventKind EventKind { get; set; }

    /// <summary>
    /// UTC instant the event occurred. Always <c>DateTimeKind.Utc</c> — the writer
    /// stamps <c>ICnasTimeProvider.UtcNow</c>. CLAUDE.md cross-cutting "UTC Everywhere".
    /// </summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>
    /// FK to the <c>UserProfile.Id</c> of the human actor that triggered the event,
    /// or <c>null</c> for system-generated events (SLA sweeps, job-driven reassigns).
    /// </summary>
    public long? ActorUserId { get; set; }

    /// <summary>
    /// For <see cref="WorkflowTaskStepEventKind.Exited"/> events that encode an explicit
    /// decision (e.g. <c>"APPROVE"</c> / <c>"REJECT"</c>), the stable decision code. Null
    /// on entry / reassign / SLA-breach events. ≤ 64 chars.
    /// </summary>
    public string? DecisionCode { get; set; }

    /// <summary>
    /// Free-text human-readable note (e.g. operator comment, system reason). Optional,
    /// capped at 1000 chars. Never used as a key — purely descriptive.
    /// </summary>
    public string? Note { get; set; }
}

/// <summary>
/// R0125 / CF 16.09 — lifecycle event kinds emitted into the
/// <see cref="WorkflowTaskStepHistory"/> projection. The set is intentionally closed
/// (additions are a breaking contract change for downstream dashboards).
/// </summary>
public enum WorkflowTaskStepEventKind
{
    /// <summary>The task entered the named step (i.e. was created on that step, or advanced into it).</summary>
    Entered = 0,

    /// <summary>The task exited the named step (advancing to a sibling step or terminal node).</summary>
    Exited = 1,

    /// <summary>
    /// The task was reassigned to a different user / group while still on the same step.
    /// Carries the actor that performed the reassignment.
    /// </summary>
    Reassigned = 2,

    /// <summary>
    /// The task's SLA window elapsed while it was still on the named step. Almost always
    /// a system event (<c>ActorUserId</c> = null), emitted by the escalation sweep.
    /// </summary>
    SlaBreached = 3,

    /// <summary>The task reached a terminal state (work successfully done).</summary>
    Completed = 4,

    /// <summary>The task was cancelled (terminal state without completion).</summary>
    Cancelled = 5,
}
