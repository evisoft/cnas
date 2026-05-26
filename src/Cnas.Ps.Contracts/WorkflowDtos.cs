namespace Cnas.Ps.Contracts;

/// <summary>
/// R0129 / CF 15.04 — history-list row for the GET <c>/api/workflows/{code}/history</c>
/// endpoint. Carries version-chain metadata so administrators can render a timeline of
/// revisions for a workflow code.
/// </summary>
/// <remarks>
/// Workflow codes are NOT Sqid-encoded (CLAUDE.md RULE 3 documented exception) — see
/// <c>WorkflowsController</c> for the rationale. Each entry's <see cref="Version"/>
/// together with <see cref="Code"/> uniquely addresses the row.
/// </remarks>
/// <param name="Code">Logical workflow code (shared across the chain).</param>
/// <param name="Version">Monotonic version number — 1 on the original, N+1 on each republish.</param>
/// <param name="IsCurrent">True on exactly one row per code.</param>
/// <param name="CreatedAtUtc">When this revision was persisted.</param>
/// <param name="SupersededAtUtc">UTC instant the row stopped being current; null on the active row.</param>
public sealed record WorkflowDefinitionHistoryItem(
    string Code,
    int Version,
    bool IsCurrent,
    System.DateTime CreatedAtUtc,
    System.DateTime? SupersededAtUtc);

/// <summary>
/// R0121 / CF 16.02 — single row of the workflow-definitions list endpoint
/// (<c>GET /api/workflows</c>). Carries only the metadata required by the
/// admin visual designer to populate its list page — the JSON body is fetched
/// per-row via the existing <c>GET /api/workflows/{code}</c> when the operator
/// opens the editor.
/// </summary>
/// <remarks>
/// Workflow codes are NOT Sqid-encoded — they ARE the public identifier
/// (e.g. <c>WF-PENSION-AGE</c>); see the documented exception on
/// <c>WorkflowsController</c> / <c>WorkflowDefinition.Code</c>.
/// The <see cref="DefinitionSqid"/> field IS Sqid-encoded because it surfaces
/// the surrogate row id so a historical version can be addressed deterministically.
/// </remarks>
/// <param name="DefinitionSqid">Sqid-encoded id of the active <c>WorkflowDefinition</c> row.</param>
/// <param name="Code">Logical workflow code (e.g. <c>WF-AGE</c>).</param>
/// <param name="Version">Monotonically-increasing version number of the active row.</param>
/// <param name="IsCurrent">Always <c>true</c> for items returned by the list endpoint; reserved for future history extensions.</param>
/// <param name="CreatedAtUtc">UTC instant at which the active row was inserted.</param>
public sealed record WorkflowDefinitionListItem(
    string DefinitionSqid,
    string Code,
    int Version,
    bool IsCurrent,
    System.DateTime CreatedAtUtc);

/// <summary>
/// R0125 / CF 16.09 — one row of the workflow-task history projection. Carries a single
/// state-transition / reassignment / SLA event recorded against the task. Returned by the
/// <c>GET /api/workflow-tasks/{sqid}/history</c> endpoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sensitivity: Internal.</b> Step codes and actor sqids are visible only to
/// authorised admins; the projection is not exposed to citizens.
/// </para>
/// <para>
/// All ids are Sqid-encoded strings per CLAUDE.md RULE 3 — the projection's primary
/// <see cref="Id"/>, the actor's <see cref="ActorUserSqid"/>, and the parent
/// <see cref="WorkflowTaskSqid"/>. <see cref="EventKind"/> is the enum name (e.g.
/// <c>"Entered"</c>) so clients can render readable timelines without coupling to
/// numeric ordinals.
/// </para>
/// </remarks>
/// <param name="Id">Sqid-encoded surrogate id of the history row.</param>
/// <param name="WorkflowTaskSqid">Sqid-encoded id of the task this row belongs to.</param>
/// <param name="StepCode">Stable step code (lower-case) the task was in / transitioning into.</param>
/// <param name="EventKind">Enum name of the transition (Entered / Exited / Reassigned / SlaBreached / Completed / Cancelled).</param>
/// <param name="OccurredAt">UTC instant the event occurred.</param>
/// <param name="ActorUserSqid">Sqid-encoded user id of the actor; null for system events.</param>
/// <param name="DecisionCode">Stable decision code for Exited events; null otherwise.</param>
/// <param name="Note">Optional free-text descriptive note.</param>
public sealed record WorkflowTaskStepHistoryDto(
    string Id,
    string WorkflowTaskSqid,
    string StepCode,
    string EventKind,
    System.DateTime OccurredAt,
    string? ActorUserSqid,
    string? DecisionCode,
    string? Note);

/// <summary>
/// R0125 / CF 16.09 — paged response carrying a slice of
/// <see cref="WorkflowTaskStepHistoryDto"/> rows for the requested task. Mirrors the
/// shape of <c>PagedResult&lt;T&gt;</c> on the read side but is a fixed-shape record
/// so it can be transported through the static contracts surface without a generic
/// parameter.
/// </summary>
/// <remarks>
/// <b>Sensitivity: Internal.</b> Inherits the projection's sensitivity.
/// </remarks>
/// <param name="Items">The page of history rows (chronologically ordered ascending).</param>
/// <param name="TotalCount">Total number of history rows matching the filter (across all pages).</param>
public sealed record WorkflowTaskHistoryPageDto(
    System.Collections.Generic.IReadOnlyList<WorkflowTaskStepHistoryDto> Items,
    long TotalCount);

/// <summary>
/// R0125 / CF 16.09 — query filter for
/// <c>IWorkflowTaskHistoryService.GetHistoryAsync</c>. All fields are optional; when
/// omitted the service returns every row for the task with a default page size.
/// </summary>
/// <remarks>
/// <b>Sensitivity: Internal.</b>
/// </remarks>
/// <param name="EventKind">
/// Optional enum-name filter (e.g. <c>"Reassigned"</c>). Case-sensitive. When set the
/// response only includes rows whose <c>EventKind</c> matches.
/// </param>
/// <param name="Skip">Zero-based row offset (≥ 0). Defaults to 0 when null.</param>
/// <param name="Take">Page size (1..200). Defaults to 50 when null.</param>
public sealed record WorkflowTaskHistoryFilterDto(
    string? EventKind = null,
    int? Skip = null,
    int? Take = null);

/// <summary>
/// R0122 / TOR CF 16.07 — strongly-typed performer descriptor on the wire (input or
/// output). Mirrors the Core <c>WorkflowPerformerAssignment</c> value object but
/// stays in the Contracts layer per the layered-architecture rules (the Core type
/// itself cannot be referenced from controllers / DTOs).
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Kind"/> field carries one of the
/// <c>WorkflowPerformerKind</c> enum names (<c>Role</c> / <c>Group</c> /
/// <c>NamedUser</c> / <c>Originator</c> / <c>Supervisor</c>); the validator gates
/// the value against the enum's <c>Names</c> set.
/// </para>
/// <para>
/// The <see cref="Code"/> field is REQUIRED for <c>Role</c> / <c>Group</c> /
/// <c>NamedUser</c> kinds and IGNORED for reflexive (<c>Originator</c> /
/// <c>Supervisor</c>) kinds. When <see cref="Kind"/> is <c>NamedUser</c> the code
/// MUST be a valid Sqid resolving to an active <c>UserProfile</c>.
/// </para>
/// </remarks>
/// <param name="Kind">One of the WorkflowPerformerKind enum names.</param>
/// <param name="Code">Role / group code, or Sqid (for NamedUser); null for reflexive kinds.</param>
/// <param name="FallbackKind">Optional fallback kind name.</param>
/// <param name="FallbackCode">Optional fallback code paired with <paramref name="FallbackKind"/>.</param>
public sealed record WorkflowPerformerAssignmentDto(
    string Kind,
    string? Code,
    string? FallbackKind = null,
    string? FallbackCode = null);

/// <summary>
/// R0122 / TOR CF 16.07 — strongly-typed SLA descriptor on the wire. Mirrors the
/// Core <c>WorkflowStepSla</c> value object using JSON-friendly numeric durations
/// in minutes (avoids ISO-8601 parsing drift across clients).
/// </summary>
/// <remarks>
/// Both <see cref="DueWithinMinutes"/> and <see cref="EscalateAfterMinutes"/> are
/// expressed in whole minutes and must satisfy <c>DueWithinMinutes &gt; 0</c> and
/// <c>EscalateAfterMinutes &gt;= DueWithinMinutes</c>; the validator enforces both.
/// </remarks>
/// <param name="DueWithinMinutes">Time budget from creation to "must complete by", in minutes.</param>
/// <param name="EscalateAfterMinutes">Time budget from creation to escalation, in minutes (&gt;= DueWithinMinutes).</param>
/// <param name="BusinessHoursOnly">When true, both windows are measured against business hours.</param>
public sealed record WorkflowStepSlaDto(
    int DueWithinMinutes,
    int EscalateAfterMinutes,
    bool BusinessHoursOnly);
