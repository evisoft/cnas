namespace Cnas.Ps.Contracts;

/// <summary>
/// R0127 / CF 16.11 — input body for <c>POST /api/tasks/{taskSqid}/reassign</c>. Carries
/// the delegate's Sqid + a free-text reason captured for the audit trail.
/// </summary>
/// <param name="NewAssigneeSqid">
/// Sqid-encoded id of the user to whom the task is being delegated. Decoded server-side;
/// malformed values surface as <c>ErrorCodes.InvalidSqid</c>.
/// </param>
/// <param name="Reason">
/// Free-text justification (e.g. <c>"Concediu medical"</c>). 3..500 chars per validator;
/// stored verbatim on the task and on the audit row.
/// </param>
public sealed record WorkflowTaskReassignDto(
    string NewAssigneeSqid,
    string Reason);

/// <summary>
/// R0127 / CF 16.11 — input body for <c>POST /api/user-absences</c>. Plans an absence
/// for the supplied user, nominating a delegate to receive their open tasks for the
/// duration.
/// </summary>
/// <param name="UserSqid">Sqid-encoded id of the absent user.</param>
/// <param name="StartDateUtc">Inclusive start of the absence window, UTC.</param>
/// <param name="EndDateUtc">Inclusive end of the absence window, UTC.</param>
/// <param name="DelegateSqid">
/// Sqid-encoded id of the delegate who receives the absent user's open tasks for the
/// duration. MUST differ from <see cref="UserSqid"/> — the validator rejects identical
/// values.
/// </param>
/// <param name="Reason">
/// Free-text reason captured at planning time. 3..200 chars per validator; persisted
/// verbatim on the row.
/// </param>
public sealed record UserAbsenceCreateDto(
    string UserSqid,
    DateTime StartDateUtc,
    DateTime EndDateUtc,
    string DelegateSqid,
    string Reason);

/// <summary>
/// R0127 / CF 16.11 — output projection of a <c>UserAbsence</c>
/// row. Every <c>*Sqid</c> field is the Sqid-encoded form of the corresponding raw
/// <c>long</c> primary key (CLAUDE.md RULE 3); the lifecycle status is stringified to a
/// stable code so a client that decodes JSON does not couple to numeric enum values.
/// </summary>
/// <param name="Id">Sqid-encoded id of this absence row.</param>
/// <param name="UserSqid">Sqid-encoded id of the absent user.</param>
/// <param name="DelegateSqid">Sqid-encoded id of the delegate user.</param>
/// <param name="StartDateUtc">Inclusive start of the absence window.</param>
/// <param name="EndDateUtc">Inclusive end of the absence window.</param>
/// <param name="Status">
/// Stringified lifecycle status — <c>Planned</c>, <c>Active</c>, <c>Completed</c>, or
/// <c>Cancelled</c>. Stable across deployments.
/// </param>
/// <param name="ActivatedAtUtc">UTC instant of the <c>Planned → Active</c> flip, or null.</param>
/// <param name="CompletedAtUtc">UTC instant of the <c>Active → Completed</c> flip, or null.</param>
/// <param name="RoutedTaskCount">Informational count of tasks routed at activation time.</param>
/// <param name="Reason">Free-text reason captured at planning time.</param>
public sealed record UserAbsenceOutputDto(
    string Id,
    string UserSqid,
    string DelegateSqid,
    DateTime StartDateUtc,
    DateTime EndDateUtc,
    string Status,
    DateTime? ActivatedAtUtc,
    DateTime? CompletedAtUtc,
    int RoutedTaskCount,
    string Reason);

/// <summary>
/// R0127 / CF 16.11 — output projection of a <c>WorkflowTask</c>
/// after a reassignment operation. Distinct from <see cref="TaskInboxItem"/> because the
/// reassignment surface needs to surface the original-assignee anchor and the
/// reassignment counter, which the inbox row hides.
/// </summary>
/// <param name="Id">Sqid-encoded workflow-task id.</param>
/// <param name="Title">Display title of the task.</param>
/// <param name="Status">Stringified <c>WorkflowTaskStatus</c>.</param>
/// <param name="AssigneeSqid">Sqid-encoded id of the CURRENT assignee, or null when in a group inbox.</param>
/// <param name="OriginalAssigneeSqid">
/// Sqid-encoded id of the original assignee when the task has been reassigned at least
/// once; <c>null</c> when the task has never been reassigned.
/// </param>
/// <param name="DelegatedFromAbsenceSqid">
/// Sqid-encoded id of the absence row that routed this task to the delegate, or null
/// when the reassignment was per-task or the task has never been reassigned.
/// </param>
/// <param name="ReassignmentCount">Monotonic count of reassignments + reverts since creation.</param>
/// <param name="ReassignmentReason">Free-text reason captured at the most recent reassignment.</param>
public sealed record WorkflowTaskOutputDto(
    string Id,
    string Title,
    string Status,
    string? AssigneeSqid,
    string? OriginalAssigneeSqid,
    string? DelegatedFromAbsenceSqid,
    int ReassignmentCount,
    string? ReassignmentReason);
