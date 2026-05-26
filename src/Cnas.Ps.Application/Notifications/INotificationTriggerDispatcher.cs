using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Notifications;

/// <summary>
/// R0174 / TOR CF 22.03 — closed vocabulary of the five canonical notification
/// triggers the platform fires from its workflow / decision / job pipelines.
/// </summary>
/// <remarks>
/// <para>
/// <b>One enum, one call site.</b> Each value pairs with exactly one originating
/// service call: task-assignment lands from <c>WorkflowTaskService</c>,
/// SLA-breach lands from <c>DossierSlaMonitorJob</c>, approval-needed from the
/// approval state-machine, action-result from the decider service, and
/// performance-alert from the background-job overrun watcher. The enum exists
/// so the dispatcher can stamp a single canonical event-kind onto the persisted
/// notification row for analytics / audit drill-down.
/// </para>
/// <para>
/// <b>Numeric stability.</b> Values are deliberately gapped by 1; the underlying
/// number is NOT persisted today (the kind is collapsed into the subject /
/// related-entity columns), but adding a column later would be additive — never
/// renumber existing values.
/// </para>
/// </remarks>
public enum NotificationTriggerKind
{
    /// <summary>CF 22.03.a — a workflow task is assigned to a user (or reassigned).</summary>
    TaskAssignment = 0,

    /// <summary>CF 22.03.b — an SLA-tracked task crossed its <c>DueAtUtc</c> threshold.</summary>
    SlaBreach = 1,

    /// <summary>CF 22.03.c — a workflow step requires approval and the approver is set.</summary>
    ApprovalNeeded = 2,

    /// <summary>CF 22.03.d — a decision (approve/reject/withdraw) reached a terminal state.</summary>
    ActionResult = 3,

    /// <summary>CF 22.03.e — a long-running job exceeded its expected duration threshold.</summary>
    PerformanceAlert = 4,
}

/// <summary>
/// R0174 / TOR CF 22.03 — payload of a single notification trigger. Carries the
/// recipient, the human-readable subject + body, and the optional related-entity
/// pair the inbox UI uses to compose the deep-link via
/// <see cref="INotificationDeepLinkResolver"/>.
/// </summary>
/// <param name="RecipientUserId">
/// Internal <c>UserProfile.Id</c> of the user the notification is addressed to.
/// Required — anonymous broadcasts are not modelled by this surface.
/// </param>
/// <param name="Subject">One-line subject (already localised by the caller).</param>
/// <param name="Body">Multi-line body (already localised by the caller).</param>
/// <param name="CorrelationId">
/// Optional correlation id linking the notification back to the originating
/// request / workflow event. Forwarded verbatim onto the persisted row.
/// </param>
/// <param name="RelatedEntityType">
/// Optional stable vocabulary from <see cref="NotificationRelatedEntityTypes"/>.
/// When set together with <paramref name="RelatedEntityId"/> the inbox renders
/// the subject as a clickable deep-link (see R0172).
/// </param>
/// <param name="RelatedEntityId">
/// Optional raw <see cref="long"/> primary key of the related business object.
/// Sqid encoding happens at the inbox DTO boundary — never here.
/// </param>
public sealed record NotificationTriggerPayload(
    long RecipientUserId,
    string Subject,
    string Body,
    string? CorrelationId,
    string? RelatedEntityType,
    long? RelatedEntityId);

/// <summary>
/// R0174 / TOR CF 22.03 — single seam through which every workflow / decision /
/// job site fires one of the five canonical notification triggers. Behind the
/// interface the implementation funnels the payload through the existing
/// <see cref="UseCases.INotificationService"/> so the inbox + MNotify mirror
/// + deep-link resolver (R0172) all pick the row up uniformly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Best-effort.</b> Implementations MUST NOT throw on transient failures;
/// they return a <see cref="Result"/> failure so the caller can decide whether
/// to log or ignore. Callers in turn MUST NOT roll back their primary
/// state-machine mutation on a failed dispatch — a missed notification is
/// a UX regression, not a data-corruption event.
/// </para>
/// <para>
/// <b>Lifetime.</b> Scoped — the default implementation captures the per-request
/// <see cref="UseCases.INotificationService"/> + caller context, so it inherits
/// the per-request DbContext scope.
/// </para>
/// </remarks>
public interface INotificationTriggerDispatcher
{
    /// <summary>
    /// Dispatches one canonical trigger. The <paramref name="kind"/> stamps the
    /// notification's semantic class; the <paramref name="payload"/> carries the
    /// recipient + body + optional related-entity pair.
    /// </summary>
    /// <param name="kind">One of the five CF 22.03 trigger kinds.</param>
    /// <param name="payload">Recipient + body + related-entity envelope.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> when the row was persisted, a failure with
    /// the underlying error code from <see cref="UseCases.INotificationService"/>
    /// otherwise.
    /// </returns>
    Task<Result> DispatchAsync(
        NotificationTriggerKind kind,
        NotificationTriggerPayload payload,
        CancellationToken cancellationToken = default);
}
