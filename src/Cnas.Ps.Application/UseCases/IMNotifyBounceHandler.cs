using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R0115 / TOR CF 14.07 — handler for inbound MNotify bounce / delivery-failure
/// webhooks. Locates the <c>Notification</c> row by upstream reference, flips
/// <c>DeliveryStatus</c> to <see cref="Cnas.Ps.Core.Domain.NotificationDeliveryStatus.Failed"/>,
/// persists the bounce metadata, and writes a <c>NOTIFY.BOUNCED</c> audit row.
/// Idempotent — a second call for the same reference is a no-op success.
/// </summary>
public interface IMNotifyBounceHandler
{
    /// <summary>
    /// Processes a single bounce notification.
    /// </summary>
    /// <param name="payload">Webhook payload posted by the MNotify gateway.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on success;
    /// <see cref="Result.Failure"/> with <see cref="ErrorCodes.NotFound"/> when
    /// the referenced notification cannot be located.
    /// </returns>
    Task<Result> HandleBounceAsync(
        MNotifyBounceWebhookPayload payload,
        CancellationToken cancellationToken = default);
}
