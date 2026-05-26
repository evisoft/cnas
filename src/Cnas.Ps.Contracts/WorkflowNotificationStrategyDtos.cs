namespace Cnas.Ps.Contracts;

/// <summary>
/// R0128 / R0173 — single row of the per-workflow notification-strategy registry. All
/// id fields are Sqid-encoded strings per CLAUDE.md RULE 3. Channel and recipient-role
/// lists are stable string projections (no enum references in this assembly) so the
/// Contracts project stays Core-free.
/// </summary>
/// <param name="Id">Sqid-encoded id of the strategy row.</param>
/// <param name="WorkflowDefinitionId">Sqid-encoded id of the workflow definition this strategy governs.</param>
/// <param name="EventCode">
/// Stable workflow event code (e.g. <c>Task.Assigned</c>, <c>Task.Completed</c>,
/// <c>Task.Overdue</c>, <c>Workflow.Started</c>). Must be one of the canonical values
/// listed in <c>Cnas.Ps.Application.WorkflowNotifications.WorkflowNotificationEvents</c>.
/// </param>
/// <param name="IsEnabled">
/// When <c>true</c>, the strategy is the active dispatch instruction. When <c>false</c>,
/// the orchestrator suppresses ALL notifications for this (workflow, event) pair and
/// increments the suppressed counter — the row is the explicit "do not notify" override.
/// </param>
/// <param name="Channels">
/// Stable string forms of <c>NotificationChannel</c> (<c>Email</c>, <c>Sms</c>,
/// <c>InApp</c>) the orchestrator dispatches on. The validator requires at least one
/// entry when <see cref="IsEnabled"/> is <c>true</c>.
/// </param>
/// <param name="RecipientRoles">
/// Recipient role codes from the frozen allow-list. Each entry matches
/// <c>^(Assignee|AssigneeSupervisor|Applicant|ProcessOwner|ApprovingManager|CustomGroup:[a-zA-Z0-9._-]{1,64})$</c>.
/// </param>
/// <param name="TemplateCodeOverride">
/// Optional template code override; when null the orchestrator falls back to the
/// deterministic default template for the event code.
/// </param>
/// <param name="QuietHoursStart">
/// Local-time inclusive start of the quiet-hours window expressed as minutes-of-day
/// (0..1439); null when no quiet hours apply.
/// </param>
/// <param name="QuietHoursEnd">
/// Local-time inclusive end of the quiet-hours window expressed as minutes-of-day
/// (0..1439); null when no quiet hours apply.
/// </param>
/// <param name="Description">Free-form admin-facing description; nullable.</param>
public sealed record WorkflowNotificationStrategyOutput(
    string Id,
    string WorkflowDefinitionId,
    string EventCode,
    bool IsEnabled,
    IReadOnlyList<string> Channels,
    IReadOnlyList<string> RecipientRoles,
    string? TemplateCodeOverride,
    int? QuietHoursStart,
    int? QuietHoursEnd,
    string? Description);

/// <summary>
/// Request body for the upsert endpoint
/// <c>PUT /api/workflow-definitions/{workflowSqid}/notification-strategies/{eventCode}</c>.
/// The workflow id + event code live in the route; only the configuration fields appear
/// in the body. Mass-assignment protection (CLAUDE.md §2.4) is enforced by the route's
/// authorization policy + the absence of any audit / id / system fields on this input.
/// </summary>
/// <param name="IsEnabled">
/// Explicit on/off flag. <c>false</c> is the documented "do not notify" override.
/// </param>
/// <param name="Channels">
/// Stable channel string list (<c>Email</c>, <c>Sms</c>, <c>InApp</c>). Required to be
/// non-empty when <see cref="IsEnabled"/> is <c>true</c>; validator rejects the
/// inconsistency.
/// </param>
/// <param name="RecipientRoles">
/// Recipient role codes from the allow-list described on
/// <see cref="WorkflowNotificationStrategyOutput.RecipientRoles"/>.
/// </param>
/// <param name="TemplateCodeOverride">
/// Optional template code override; null preserves the default-by-event-code template.
/// </param>
/// <param name="QuietHoursStart">
/// Local-time inclusive start of the quiet-hours window (0..1439); null when no quiet
/// hours apply. Must be paired with <see cref="QuietHoursEnd"/> — validator rejects
/// half-set pairs.
/// </param>
/// <param name="QuietHoursEnd">
/// Local-time inclusive end of the quiet-hours window (0..1439); null when no quiet
/// hours apply.
/// </param>
/// <param name="Description">Optional admin-facing description.</param>
public sealed record WorkflowNotificationStrategyUpsertInput(
    bool IsEnabled,
    IReadOnlyList<string> Channels,
    IReadOnlyList<string> RecipientRoles,
    string? TemplateCodeOverride,
    int? QuietHoursStart,
    int? QuietHoursEnd,
    string? Description);
