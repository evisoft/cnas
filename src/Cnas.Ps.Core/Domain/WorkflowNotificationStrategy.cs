using System.Collections.Generic;

namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0128 / R0173 — admin-configurable per-workflow notification override consulted by
/// <c>IWorkflowNotificationOrchestrator</c> on every workflow lifecycle event. Each row
/// expresses "for this workflow definition, when event X fires, dispatch on these
/// channels to these recipient roles using this template (or suppress entirely)".
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this entity exists.</b> Before R0128 every workflow task assignment triggered
/// the same Email + InApp dispatch to the assignee. R0128 / TOR CF 16.14 + CF 22.04
/// require per-workflow strategies — pension workflows might fan out to the assignee's
/// supervisor, while child-allowance workflows might silence in-app notifications during
/// quiet hours. Operators tune this without touching code via the admin REST surface
/// (<c>WorkflowNotificationStrategiesController</c>); changes take effect within one
/// cache refresh tick (60 s default) or instantly when the CRUD service triggers a
/// synchronous resolver invalidation.
/// </para>
/// <para>
/// <b>Natural key.</b> Each (WorkflowDefinitionId, EventCode) tuple is unique — one
/// strategy per (workflow, event). The CRUD service's <c>UpsertAsync</c> applies this
/// invariant idempotently; the database unique index is the safety net.
/// </para>
/// <para>
/// <b>Two off-switches.</b> <see cref="IsEnabled"/> is the explicit "we considered this
/// event and the operator chose to NOT notify" override — a row with
/// <c>IsEnabled = false</c> is the documented suppression instruction, and the
/// orchestrator increments <c>cnas.workflow.notify.suppressed</c> when it observes one.
/// <see cref="AuditableEntity.IsActive"/> is the standard soft-delete flag for CRUD
/// history: an operator who removes a strategy via <c>DELETE</c> flips <c>IsActive</c>
/// to <c>false</c> but the row remains queryable for the audit explorer. The resolver's
/// snapshot loads only rows where <c>IsActive = true</c>; <c>IsEnabled</c> is inspected
/// by the orchestrator after the resolver returns.
/// </para>
/// <para>
/// <b>Quiet hours.</b> When both <see cref="QuietHoursStartLocalMinute"/> and
/// <see cref="QuietHoursEndLocalMinute"/> are non-null, an event firing within the local
/// (Europe/Chisinau) window is DEFERRED — the resulting <c>Notification</c> row carries
/// a <c>ScheduledDispatchAtUtc</c> equal to the next end-of-window instant. The window
/// MAY wrap midnight (e.g. <c>22:00..06:00</c>); the orchestrator handles the wrap by
/// testing membership rather than ordering. When both are null no quiet hours apply.
/// </para>
/// <para>
/// <b>External id contract.</b> Implements <see cref="IExternalId"/> because the CRUD
/// service exposes strategies over an admin REST surface and consumers reference rows
/// by their Sqid-encoded id. The workflow id round-trip uses the workflow definition's
/// Sqid; the natural key (workflow + event) stays internal.
/// </para>
/// </remarks>
public sealed class WorkflowNotificationStrategy : AuditableEntity, IExternalId
{
    /// <summary>
    /// FK to the <see cref="WorkflowDefinition"/> whose tasks this strategy governs. The
    /// strategy applies to ALL versions of the workflow definition for the given code;
    /// the resolver looks up by id at runtime, so a new revision shares the same
    /// strategy unless an operator explicitly rebinds it.
    /// </summary>
    public long WorkflowDefinitionId { get; set; }

    /// <summary>
    /// Stable event code identifying the workflow lifecycle moment that this strategy
    /// governs. Must match one of the canonical values in
    /// <c>WorkflowNotificationEvents</c> (e.g. <c>Task.Assigned</c>,
    /// <c>Task.Completed</c>, <c>Task.Overdue</c>). Validator enforces the allow-list;
    /// the database column caps at 64 characters.
    /// </summary>
    public required string EventCode { get; set; }

    /// <summary>
    /// Explicit on/off switch — distinct from soft-delete. When <c>false</c>, the
    /// orchestrator increments the suppressed counter and returns success without
    /// dispatching ANY notifications for this (workflow, event) pair. Operators flip
    /// this to <c>false</c> when they want a documented "we considered notifying and
    /// chose not to" record on the books; the row remains visible in the admin UI so
    /// the decision is auditable.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Channels the orchestrator dispatches on when <see cref="IsEnabled"/> is true. The
    /// validator rejects an empty list paired with <c>IsEnabled = true</c> — a strategy
    /// that fires but has no channels is a misconfiguration. Stored as <c>jsonb</c> on
    /// PostgreSQL (mirrors <see cref="AuditPolicy.ExtraRedactKeys"/>); the value
    /// converter materialises the list with case-sensitive ordinal comparison.
    /// </summary>
    public List<NotificationChannel> Channels { get; set; } = new();

    /// <summary>
    /// Recipient roles the orchestrator resolves to actual <see cref="UserProfile"/> ids
    /// at dispatch time. Each entry must match the regex
    /// <c>^(Assignee|AssigneeSupervisor|Applicant|ProcessOwner|ApprovingManager|CustomGroup:[a-zA-Z0-9._-]{1,64})$</c>;
    /// the validator enforces the allow-list. Resolution failures (e.g. supervisor
    /// relation not configured) are logged + skipped — they do NOT fail the dispatch.
    /// Stored as a JSON string list on PostgreSQL mirroring
    /// <see cref="Channels"/>.
    /// </summary>
    public List<string> RecipientRoles { get; set; } = new();

    /// <summary>
    /// When non-null, replaces the default notification template the orchestrator
    /// would otherwise pick for this (workflow, event) pair. The orchestrator hands
    /// the override to the dispatch step so the rendered subject/body comes from the
    /// configured template instead of the deterministic default. Capped at 64
    /// characters at the EF mapping layer; null means "use the default for this event
    /// code".
    /// </summary>
    public string? TemplateCodeOverride { get; set; }

    /// <summary>
    /// Local-time (Europe/Chisinau) inclusive start of the quiet-hours window,
    /// expressed as minutes-of-day in the range 0..1439. When non-null the orchestrator
    /// defers any dispatch falling inside the window until <see cref="QuietHoursEndLocalMinute"/>.
    /// Pairing rule: both Start and End MUST be set together OR both null; the validator
    /// enforces. Wrapping windows (Start &gt; End, e.g. 22:00..06:00) are supported.
    /// </summary>
    public int? QuietHoursStartLocalMinute { get; set; }

    /// <summary>
    /// Local-time (Europe/Chisinau) inclusive end of the quiet-hours window. See
    /// <see cref="QuietHoursStartLocalMinute"/> for the pairing rule and the wrap
    /// semantics.
    /// </summary>
    public int? QuietHoursEndLocalMinute { get; set; }

    /// <summary>
    /// Free-form admin-facing description of why this strategy exists. Surfaces in the
    /// admin UI + in the audit trail of mutations. Capped at 512 characters at the EF
    /// mapping layer.
    /// </summary>
    public string? Description { get; set; }
}
