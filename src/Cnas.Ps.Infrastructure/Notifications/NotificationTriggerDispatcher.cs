using Cnas.Ps.Application.Notifications;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Notifications;

/// <summary>
/// R0174 / TOR CF 22.03 — default <see cref="INotificationTriggerDispatcher"/>
/// implementation. Builds a single anchored notification row via
/// <see cref="INotificationService.EnqueueAsync(long, string, string, string?, string?, long?, CancellationToken)"/>
/// so every canonical trigger surfaces uniformly on the inbox + MNotify mirror
/// + dashboard tile (R0170).
/// </summary>
/// <remarks>
/// <para>
/// The dispatcher is a thin adapter — no business decisions live here. The kind
/// is currently used only to disambiguate trigger sites at the call site (and
/// for future per-kind metrics). The row's semantic class is encoded by the
/// caller through <see cref="NotificationTriggerPayload.RelatedEntityType"/>:
/// task-assignment / SLA-breach / approval pin to <c>WorkflowTask</c> /
/// <c>Dossier</c>, action-result to <c>Application</c>, performance-alert to
/// <c>ReportRun</c>.
/// </para>
/// <para>
/// <b>Lifetime.</b> Scoped — captures the per-request notification service so
/// the per-request <c>ICnasDbContext</c> + <c>ICallerContext</c> propagate
/// naturally.
/// </para>
/// </remarks>
public sealed class NotificationTriggerDispatcher(INotificationService notifications)
    : INotificationTriggerDispatcher
{
    private readonly INotificationService _notifications = notifications;

    /// <inheritdoc />
    public Task<Result> DispatchAsync(
        NotificationTriggerKind kind,
        NotificationTriggerPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _ = kind; // Reserved for per-kind metrics — see <remarks>.

        return _notifications.EnqueueAsync(
            recipientUserId: payload.RecipientUserId,
            subject: payload.Subject,
            body: payload.Body,
            correlationId: payload.CorrelationId,
            relatedEntityType: payload.RelatedEntityType,
            relatedEntityId: payload.RelatedEntityId,
            cancellationToken: cancellationToken);
    }
}
