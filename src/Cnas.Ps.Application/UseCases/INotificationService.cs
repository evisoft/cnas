using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>UC22 — Notify users. Combines in-app inbox with MNotify dispatch.</summary>
public interface INotificationService
{
    /// <summary>
    /// Returns the calling user's inbox using the legacy "all rows, paged" contract.
    /// Kept for backwards compatibility with callers that don't filter — the dashboard
    /// history view should prefer the <see cref="InboxAsync(NotificationInboxQuery, System.Threading.CancellationToken)"/>
    /// overload instead.
    /// </summary>
    /// <param name="page">Pagination input.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged inbox or a failure code.</returns>
    Task<Result<PagedResult<NotificationOutput>>> InboxAsync(PageRequest page, CancellationToken cancellationToken = default);

    /// <summary>
    /// R0371 — extended-filter inbox query. Returns the calling user's notification
    /// history filtered by read state + channel, with pagination matching
    /// <see cref="PageRequest"/>. The filter parameters are validated server-side;
    /// unknown channel codes surface as <see cref="ErrorCodes.ValidationFailed"/>.
    /// </summary>
    /// <param name="query">Filter + pagination envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged inbox or a failure code.</returns>
    Task<Result<PagedResult<NotificationOutput>>> InboxAsync(NotificationInboxQuery query, CancellationToken cancellationToken = default);

    /// <summary>Marks a notification as read.</summary>
    /// <param name="input">DTO carrying the Sqid-encoded notification id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see cref="Result.Success"/> on success; <see cref="ErrorCodes.NotFound"/> when the row is missing.</returns>
    Task<Result> MarkReadAsync(MarkNotificationReadInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// R0371 — bulk-marks every unread inbox row for the calling user as read. Rows
    /// already carrying a <c>ReadAtUtc</c> stamp are left untouched. Returns the count
    /// of rows actually flipped so the UI can show a confirmation.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows transitioned to read on success.</returns>
    Task<Result<int>> MarkAllReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Enqueues a notification for delivery (in-app + MNotify channels).</summary>
    /// <param name="recipientUserId">Internal id of the recipient.</param>
    /// <param name="subject">One-line subject.</param>
    /// <param name="body">Localised body text.</param>
    /// <param name="correlationId">Optional correlation id linking back to the originating workflow event.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success or a failure code.</returns>
    Task<Result> EnqueueAsync(long recipientUserId, string subject, string body, string? correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// R0174 / TOR CF 22.03 — overload that lets the caller anchor the notification
    /// to a business object so the inbox UI can render a deep-link via
    /// <c>INotificationDeepLinkResolver</c> (R0172). The anchored pair is persisted
    /// onto the underlying <c>Notification.RelatedEntityType</c> /
    /// <c>Notification.RelatedEntityId</c> columns. Pass <c>null</c> for either
    /// side to skip the deep-link (the row then renders as plain text).
    /// </summary>
    /// <param name="recipientUserId">Internal id of the recipient.</param>
    /// <param name="subject">One-line subject.</param>
    /// <param name="body">Localised body text.</param>
    /// <param name="correlationId">Optional correlation id.</param>
    /// <param name="relatedEntityType">
    /// Optional stable vocabulary from <c>NotificationRelatedEntityTypes</c>.
    /// </param>
    /// <param name="relatedEntityId">
    /// Optional raw <see cref="long"/> primary key of the related object. Sqid
    /// encoding happens at the inbox DTO boundary — never here.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success or a failure code.</returns>
    Task<Result> EnqueueAsync(
        long recipientUserId,
        string subject,
        string body,
        string? correlationId,
        string? relatedEntityType,
        long? relatedEntityId,
        CancellationToken cancellationToken = default);
}
