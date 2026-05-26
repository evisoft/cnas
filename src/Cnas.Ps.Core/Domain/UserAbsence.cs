namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0127 / CF 16.11 — operator-declared absence window for a CNAS staff member with a
/// nominated delegate who receives that user's open <see cref="WorkflowTask"/> rows for
/// the duration. Lifecycle:
/// <c>Planned → Active → Completed</c>; <c>Planned → Cancelled</c> is the only other
/// terminal path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Activation.</b> When the <c>UserAbsenceLifecycleJob</c> sees a <c>Planned</c> row
/// whose <see cref="StartDateUtc"/> has been reached it calls
/// <c>IUserAbsenceService.ActivateAsync</c>, which scans every open task assigned to
/// <see cref="UserUserId"/> (Status ∈ Pending, InProgress, Overdue), captures the
/// original assignee on <see cref="WorkflowTask.OriginalAssigneeUserId"/>, flips the
/// task's <see cref="WorkflowTask.AssignedUserId"/> to <see cref="DelegateUserId"/>,
/// and stamps <see cref="WorkflowTask.DelegatedFromAbsenceId"/> with this row's id. The
/// counter <see cref="RoutedTaskCount"/> is incremented per routed row.
/// </para>
/// <para>
/// <b>Completion.</b> When the job sees an <c>Active</c> row past
/// <see cref="EndDateUtc"/> it calls <c>IUserAbsenceService.CompleteAsync</c>, which
/// reverts every still-open task whose <see cref="WorkflowTask.DelegatedFromAbsenceId"/>
/// matches this row back to its <see cref="WorkflowTask.OriginalAssigneeUserId"/>.
/// Tasks the delegate already touched (assignee changed by hand, or task completed) are
/// left as-is — the absence does not "steal" them back.
/// </para>
/// <para>
/// <b>Backdating guard.</b> Validators reject planning an absence whose
/// <see cref="StartDateUtc"/> is more than 7 days in the past — otherwise an operator
/// could rewrite the audit trail retroactively. Cancelling an absence is only valid in
/// the <c>Planned</c> state; an <c>Active</c> row must be Completed instead so the
/// revert sweep runs.
/// </para>
/// </remarks>
public sealed class UserAbsence : AuditableEntity, IExternalId
{
    /// <summary>FK to the absent <c>UserProfile</c>.</summary>
    public long UserUserId { get; set; }

    /// <summary>FK to the delegate <c>UserProfile</c> who receives the absent user's tasks.</summary>
    public long DelegateUserId { get; set; }

    /// <summary>
    /// Inclusive start of the absence window, UTC midnight. The lifecycle job activates
    /// the row when <c>ICnasTimeProvider.UtcNow</c> is at or past this stamp.
    /// </summary>
    public DateTime StartDateUtc { get; set; }

    /// <summary>
    /// Inclusive end of the absence window, UTC end-of-day (23:59:59.9999999). The
    /// lifecycle job completes the row when <c>UtcNow</c> is strictly past this stamp,
    /// so the absence covers the full final day.
    /// </summary>
    public DateTime EndDateUtc { get; set; }

    /// <summary>
    /// Free-text reason supplied at planning time (e.g. <c>"Concediu medical"</c>,
    /// <c>"Concediu de odihnă"</c>, <c>"Delegare oficială"</c>). 3..200 characters per
    /// validator; never null.
    /// </summary>
    public required string Reason { get; set; }

    /// <summary>Lifecycle state — see <see cref="UserAbsenceStatus"/> for the transition matrix.</summary>
    public UserAbsenceStatus Status { get; set; } = UserAbsenceStatus.Planned;

    /// <summary>
    /// UTC instant the lifecycle job (or an admin) flipped the row from
    /// <see cref="UserAbsenceStatus.Planned"/> to <see cref="UserAbsenceStatus.Active"/>.
    /// <c>null</c> while still planned or cancelled.
    /// </summary>
    public DateTime? ActivatedAtUtc { get; set; }

    /// <summary>
    /// UTC instant the lifecycle job flipped the row from
    /// <see cref="UserAbsenceStatus.Active"/> to <see cref="UserAbsenceStatus.Completed"/>.
    /// <c>null</c> while not yet completed.
    /// </summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Informational counter — number of <see cref="WorkflowTask"/> rows actually routed
    /// to the delegate at activation time. Read by dashboards; never decremented when
    /// tasks are reverted at completion.
    /// </summary>
    public int RoutedTaskCount { get; set; }
}
