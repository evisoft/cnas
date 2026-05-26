namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Notificare — message dispatched to one or more recipients on a business event. TOR §2.3 #10, UC22.
/// </summary>
public sealed class Notification : AuditableEntity, IExternalId
{
    /// <summary>Recipient user id (Sqid not stored — internal FK).</summary>
    public long RecipientUserId { get; set; }

    /// <summary>Channel used to deliver the notification.</summary>
    public NotificationChannel Channel { get; set; }

    /// <summary>
    /// Authoritative delivery outcome for this notification. New rows default to
    /// <see cref="NotificationDeliveryStatus.Pending"/>; the dispatcher flips the value to
    /// <see cref="NotificationDeliveryStatus.Delivered"/> on success (also stamping
    /// <see cref="DispatchedAtUtc"/>), to <see cref="NotificationDeliveryStatus.Failed"/>
    /// when the channel adapter returns an error, and to
    /// <see cref="NotificationDeliveryStatus.Suppressed"/> when policy (opt-out, quiet
    /// hours, suppression list) blocks the attempt before it reaches the channel. The
    /// Annex 6g report (<c>RPT-NOTIFICATIONS-DELIVERY</c>) groups exclusively by this field.
    /// </summary>
    public NotificationDeliveryStatus DeliveryStatus { get; set; } = NotificationDeliveryStatus.Pending;

    /// <summary>Subject (one-line summary) of the notification.</summary>
    public required string Subject { get; set; }

    /// <summary>Localised body. Templated by the originating workflow step.</summary>
    public required string Body { get; set; }

    /// <summary>
    /// UTC timestamp captured when the dispatcher successfully delivered the notification.
    /// Co-set with <see cref="DeliveryStatus"/> = <see cref="NotificationDeliveryStatus.Delivered"/>;
    /// remains <c>null</c> for Pending / Failed / Suppressed rows. The authoritative
    /// "did delivery happen?" signal lives on <see cref="DeliveryStatus"/> — this timestamp
    /// is kept purely for reporting and audit.
    /// </summary>
    public DateTime? DispatchedAtUtc { get; set; }

    /// <summary>UTC timestamp the user marked the in-app notification as read.</summary>
    public DateTime? ReadAtUtc { get; set; }

    /// <summary>Correlation id linking this notification to the originating workflow event.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// R0172 / TOR CF 22.05 — stable string vocabulary identifying the
    /// business object this notification refers to (e.g.
    /// <c>"Application"</c>, <c>"WorkflowTask"</c>, <c>"ReportRun"</c>).
    /// Combined with <see cref="RelatedEntityId"/> the inbox surface
    /// resolves a deep-link URL via
    /// <c>INotificationDeepLinkResolver.Resolve(entityType, entityId)</c>
    /// so the citizen can click the notification subject to jump straight
    /// to the underlying record. Nullable so legacy / generic notifications
    /// that have no anchoring business object render as plain text.
    /// </summary>
    public string? RelatedEntityType { get; set; }

    /// <summary>
    /// R0172 / TOR CF 22.05 — internal database id of the business object
    /// referenced by <see cref="RelatedEntityType"/>. The id is stored as a
    /// raw <see cref="long"/> for fast joins and only Sqid-encoded at the
    /// inbox-DTO boundary (CLAUDE.md RULE 3). Nullable for the same reason
    /// as <see cref="RelatedEntityType"/>.
    /// </summary>
    public long? RelatedEntityId { get; set; }
}
