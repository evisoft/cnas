using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0115 / TOR CF 14.07 — closed vocabulary of MNotify dispatch channels. Mirrors
/// <c>Cnas.Ps.Core.Domain.MNotifyChannelKind</c> at the wire boundary.
/// </summary>
public enum MNotifyChannelKindDto
{
    /// <summary>Standard email delivery — subject + HTML body.</summary>
    Email = 0,

    /// <summary>SMS message — single short-form body, no subject.</summary>
    Sms = 1,

    /// <summary>Viber push — body only.</summary>
    Viber = 2,

    /// <summary>Generic push notification (mobile app) — body only.</summary>
    Push = 3,
}

/// <summary>
/// R0115 / TOR CF 14.07 — input envelope for upserting an MNotify template.
/// </summary>
/// <param name="Code">SCREAMING_SNAKE_CASE template code (1-80 chars, dotted segments allowed).</param>
/// <param name="ChannelKind">Channel the template targets.</param>
/// <param name="Subject">Subject line; required when <paramref name="ChannelKind"/> is <see cref="MNotifyChannelKindDto.Email"/>.</param>
/// <param name="BodyMarkdown">Markdown body; required for every channel (≤ 16 KiB).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MNotifyTemplateInputDto(
    string Code,
    MNotifyChannelKindDto ChannelKind,
    string? Subject,
    string BodyMarkdown);

/// <summary>
/// R0115 / TOR CF 14.07 — read-side projection of an MNotify template.
/// </summary>
/// <param name="Sqid">Sqid-encoded id.</param>
/// <param name="Code">Stable natural-key code.</param>
/// <param name="ChannelKind">Channel the template targets.</param>
/// <param name="Subject">Subject line; nullable for SMS/Viber/Push.</param>
/// <param name="BodyMarkdown">Markdown body.</param>
/// <param name="IsActive">Soft-delete flag — <c>false</c> means the template is deactivated.</param>
/// <param name="UpdatedAtUtc">UTC instant of the most recent mutation.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MNotifyTemplateDto(
    [property: SensitivityClassification(SensitivityLabel.Public)] string Sqid,
    [property: SensitivityClassification(SensitivityLabel.Public)] string Code,
    [property: SensitivityClassification(SensitivityLabel.Public)] MNotifyChannelKindDto ChannelKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)] string? Subject,
    [property: SensitivityClassification(SensitivityLabel.Internal)] string BodyMarkdown,
    [property: SensitivityClassification(SensitivityLabel.Public)] bool IsActive,
    [property: SensitivityClassification(SensitivityLabel.Internal)] DateTime? UpdatedAtUtc);

/// <summary>
/// R0115 / TOR CF 14.07 — inbound MNotify bounce / delivery-failure webhook
/// payload. Posted by the MNotify gateway when an upstream channel returns
/// a permanent failure (mailbox full, invalid number, suppressed).
/// </summary>
/// <param name="NotificationReference">
/// Stable upstream notification reference — the value MNotify originally
/// returned from <c>POST /api/Notification</c>. Maps to the local
/// <c>Notification</c> row's external correlation id.
/// </param>
/// <param name="BounceCode">Stable SCREAMING_SNAKE_CASE bounce-cause code (e.g. <c>MAILBOX_FULL</c>).</param>
/// <param name="BounceReason">Human-readable description; never logged.</param>
/// <param name="OccurredAtUtc">UTC instant the upstream channel reported the failure.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MNotifyBounceWebhookPayload(
    string NotificationReference,
    string BounceCode,
    string? BounceReason,
    DateTime OccurredAtUtc);
