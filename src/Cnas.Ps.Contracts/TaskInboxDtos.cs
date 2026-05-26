namespace Cnas.Ps.Contracts;

/// <summary>A single workflow-task inbox row (UC05).</summary>
/// <param name="Id">
/// Sqid-encoded workflow-task identifier (CLAUDE.md RULE 3). Never the raw database
/// primary key.
/// </param>
/// <param name="Title">Display title of the task, translated at the presentation layer.</param>
/// <param name="Status">Stringified <c>WorkflowTaskStatus</c> enum value
/// (e.g. <c>Pending</c>, <c>InProgress</c>, <c>Completed</c>).</param>
/// <param name="DueAtUtc">SLA deadline in UTC. <c>null</c> when the task has no deadline.</param>
/// <param name="DossierId">
/// Sqid-encoded id of the parent <c>Dossier</c>. Allows the caller to navigate from
/// inbox to dossier without exposing internal keys.
/// </param>
public sealed record TaskInboxItem(
    string Id,
    string Title,
    string Status,
    DateTime? DueAtUtc,
    string DossierId);

/// <summary>
/// Input body for <c>POST /api/tasks/{id}/complete</c> (UC05 — CF 05.04). Carries the
/// result envelope the examiner produced (verdict, comments, attachments, ...). The
/// service stores the payload as-is; the controller does not interpret it.
/// </summary>
/// <param name="ResultJson">
/// Arbitrary JSON-serialised result payload as a raw string. Examples:
/// <c>{"verdict":"approved"}</c>, <c>{"verdict":"rejected","reason":"missing-docs"}</c>.
/// Must not be null; an empty object (<c>{}</c>) is acceptable when the task carries
/// no verdict-shaped outcome. The service does NOT validate the inner schema — that is
/// the responsibility of the workflow definition.
/// </param>
public sealed record CompleteTaskRequest(string ResultJson);

/// <summary>
/// R0381 / UC05 — supervisor-view projection of a workflow task assigned to ANY user
/// inside a group the supervisor manages. Distinct from <see cref="TaskInboxItem"/>
/// because the supervisor row surfaces the current assignee (whose Sqid + display
/// name are needed for the "reassign" action) while the citizen-inbox row hides it
/// (the citizen always IS the assignee). No PII beyond <c>AssigneeDisplayName</c> is
/// carried — IDNPs, emails, and other identity fields are deliberately omitted per
/// CLAUDE.md §5.7 and TOR SEC 035.
/// </summary>
/// <param name="Id">Sqid-encoded workflow-task identifier (CLAUDE.md RULE 3).</param>
/// <param name="Title">Display title of the task.</param>
/// <param name="Status">Stringified <c>WorkflowTaskStatus</c> value.</param>
/// <param name="DueAtUtc">SLA deadline in UTC; <c>null</c> when the task has no deadline.</param>
/// <param name="DossierId">Sqid-encoded id of the parent dossier.</param>
/// <param name="AssigneeSqid">
/// Sqid-encoded id of the user the task is currently assigned to. <c>null</c> when the
/// task is unowned in a group inbox.
/// </param>
/// <param name="AssigneeDisplayName">
/// Public-facing display name of the assignee (e.g. <c>"Ion Popescu"</c>). <c>null</c>
/// when the task has no assignee. Display-only — no other identity fields are exposed.
/// </param>
public sealed record SupervisorTeamTaskDto(
    string Id,
    string Title,
    string Status,
    DateTime? DueAtUtc,
    string DossierId,
    string? AssigneeSqid,
    string? AssigneeDisplayName);

/// <summary>
/// R0381 / CF 16.11 — input body for <c>POST /api/tasks/{taskSqid}/reassign</c> when
/// driven from the supervisor workspace. Alias for <see cref="WorkflowTaskReassignDto"/>
/// — both shapes are interchangeable; the supervisor surface adopts the wider
/// <c>TaskReassignInputDto</c> name because the spec for R0381 calls it out
/// explicitly. The field semantics, validation rules, and audit emission are
/// identical to the underlying <see cref="WorkflowTaskReassignDto"/>.
/// </summary>
/// <param name="NewAssigneeSqid">
/// Sqid-encoded id of the user the task is being reassigned to. Decoded server-side.
/// </param>
/// <param name="Reason">Free-text justification, 3..500 chars per validator.</param>
public sealed record TaskReassignInputDto(
    string NewAssigneeSqid,
    string Reason);
