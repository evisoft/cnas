namespace Cnas.Ps.Contracts;

/// <summary>
/// Stringly-typed notification channel codes used on contracts (CLAUDE.md — Contracts must
/// not reference Core types). The values mirror the <c>NotificationChannel</c> enum in
/// Core: <c>InApp</c>, <c>Email</c>, <c>Sms</c>.
/// </summary>
public static class NotificationChannelCodes
{
    /// <summary>Web inbox inside SI PS (visible after login).</summary>
    public const string InApp = "InApp";

    /// <summary>Email — dispatched via MNotify.</summary>
    public const string Email = "Email";

    /// <summary>SMS — dispatched via MNotify.</summary>
    public const string Sms = "Sms";
}

/// <summary>
/// Output for a user's notification inbox entry (UC22).
/// </summary>
/// <param name="Id">Sqid-encoded id of the notification row (CLAUDE.md RULE 3).</param>
/// <param name="Channel">Channel code (see <see cref="NotificationChannelCodes"/>): <c>InApp</c>, <c>Email</c>, or <c>Sms</c>.</param>
/// <param name="Subject">One-line subject of the notification.</param>
/// <param name="Body">Localised body text.</param>
/// <param name="CreatedAtUtc">UTC instant the row was enqueued.</param>
/// <param name="ReadAtUtc">UTC instant the user marked the row as read; null when unread.</param>
/// <param name="DeliveryStatus">
/// Stringified delivery outcome (<c>Pending</c>, <c>Delivered</c>, <c>Failed</c>, or
/// <c>Suppressed</c>). Lets the dashboard tile colour-code rows by outcome.
/// </param>
/// <param name="DeepLinkUrl">
/// R0172 / TOR CF 22.05 — relative UI route the inbox surface should render
/// the subject as a clickable link to (e.g. <c>/applications/k3Gq9</c>).
/// Computed server-side by <c>INotificationDeepLinkResolver</c> from the
/// notification's <c>RelatedEntityType</c> + <c>RelatedEntityId</c> columns;
/// <c>null</c> when the row has no related business object or the entity
/// type is not in the resolver's vocabulary. The UI MUST tolerate both
/// null and non-null values and fall back to plain-text rendering when
/// null.
/// </param>
public sealed record NotificationOutput(
    string Id,
    string Channel,
    string Subject,
    string Body,
    DateTime CreatedAtUtc,
    DateTime? ReadAtUtc,
    string DeliveryStatus,
    string? DeepLinkUrl = null);

/// <summary>Input DTO used by the user's profile pane to mark a notification as read.</summary>
public sealed record MarkNotificationReadInput(string NotificationId);

/// <summary>
/// R0371 — input query for the extended-filter notification inbox overload. Supersedes
/// the bare <see cref="PageRequest"/> overload for the dashboard history view, which
/// needs to filter by read state + channel.
/// </summary>
/// <param name="Page">Pagination input. Defaults to page 1 / 20 per page.</param>
/// <param name="UnreadOnly">
/// When <c>true</c>, returns only rows where <c>ReadAtUtc IS NULL</c>; when <c>false</c>
/// returns both read and unread rows (the default).
/// </param>
/// <param name="Channel">
/// Optional channel filter expressed as a <see cref="NotificationChannelCodes"/> string
/// (<c>InApp</c>, <c>Email</c>, <c>Sms</c>). <c>null</c> means "all channels". Service-side
/// parsing is case-insensitive and returns the stable <c>VALIDATION_FAILED</c> error code
/// for unrecognised values.
/// </param>
public sealed record NotificationInboxQuery(
    PageRequest Page,
    bool UnreadOnly = false,
    string? Channel = null);
