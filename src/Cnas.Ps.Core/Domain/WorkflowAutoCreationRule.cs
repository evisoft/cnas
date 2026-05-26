namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0540 / TOR CF 05.01 (iter 134) — admin-configurable rule that drives automatic
/// creation of <see cref="WorkflowTask"/> rows on application status transitions
/// WITHOUT requiring an external BPM engine. The rule-driven path coexists with
/// the future Operaton-driven path (R0120): once Operaton is wired,
/// <c>IsActive = false</c> can be flipped on every row to hand control over.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this entity exists.</b> The TOR / CF 05.01 requires "auto-created tasks
/// from workflow definitions". The full workflow-definition-driven path depends on
/// the external Operaton engine (epic R0120) which is gated by ops/deploy work.
/// To unblock CF 05.01 without waiting on that, iter 134 ships a lightweight
/// rule table: each row says "when application transitions from FromStatus to
/// ToStatus, create a WorkflowTask of TaskKind, assigned to a member of
/// AssigneeRole, due in DueWithinDays days". A small seeded ruleset (Draft →
/// Submitted, Submitted → UnderExamination, etc.) covers the canonical flows.
/// </para>
/// <para>
/// <b>Natural key.</b> (<see cref="FromStatus"/>, <see cref="ToStatus"/>,
/// <see cref="TaskKind"/>) — the same transition may fire MULTIPLE rules (e.g.
/// Draft → Submitted spawns both an initial-review task AND a notification
/// task), but the same (transition, kind) pair MUST NOT be duplicated.
/// </para>
/// <para>
/// <b>IsActive vs IsEnabled.</b> <see cref="AuditableEntity.IsActive"/> is the
/// standard soft-delete flag — inactive rows are NOT consulted by the
/// auto-creator. There is no separate IsEnabled column because the rule table
/// has no need for the "considered but suppressed" audit signal that
/// <see cref="WorkflowNotificationStrategy.IsEnabled"/> carries.
/// </para>
/// </remarks>
public sealed class WorkflowAutoCreationRule : AuditableEntity, IExternalId
{
    /// <summary>
    /// Application status the transition MUST be leaving for this rule to fire.
    /// Matched exactly (no wildcards). Stored as the enum int via EF Core's value
    /// converter, mirroring the rest of the codebase.
    /// </summary>
    public ApplicationStatus FromStatus { get; set; }

    /// <summary>
    /// Application status the transition MUST be entering for this rule to fire.
    /// Matched exactly. Together with <see cref="FromStatus"/> this is the
    /// trigger pattern the auto-creator queries against on every
    /// <c>TransitionStatus</c> call.
    /// </summary>
    public ApplicationStatus ToStatus { get; set; }

    /// <summary>
    /// Short kind/label of the task the rule creates (e.g. <c>"INITIAL_REVIEW"</c>,
    /// <c>"EXAMINATION"</c>, <c>"DECIDER_APPROVAL"</c>). Becomes the prefix of the
    /// <see cref="WorkflowTask.Title"/> on the created row so the inbox keeps
    /// "what kind of work is this" legible. Capped at 64 chars at the persistence
    /// layer.
    /// </summary>
    public required string TaskKind { get; set; }

    /// <summary>
    /// Role code identifying the group inbox the created task lands in (e.g.
    /// <c>"cnas-examiner"</c>, <c>"cnas-decider"</c>). The auto-creator stamps
    /// <see cref="WorkflowTask.GroupCode"/> with this value and leaves
    /// <see cref="WorkflowTask.AssignedUserId"/> null so the task surfaces in
    /// the group inbox until someone claims it. Capped at 64 chars.
    /// </summary>
    public required string AssigneeRole { get; set; }

    /// <summary>
    /// Number of calendar days after the transition instant that the auto-created
    /// task's <see cref="WorkflowTask.DueAtUtc"/> SLA stamp is set to. <c>0</c>
    /// or negative is rejected by validation — every task carries a positive SLA
    /// so the overdue-task scan (R0202 / CF 20.05) can pick it up. Typical
    /// values: 1 (immediate review), 7 (standard exam), 30 (decider approval).
    /// </summary>
    public int DueWithinDays { get; set; }
}
