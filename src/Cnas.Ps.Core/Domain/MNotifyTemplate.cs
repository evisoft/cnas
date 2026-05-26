namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0115 / TOR CF 14.07 — admin-managed registry of MNotify dispatch templates.
/// Each row binds a stable <see cref="Code"/> (e.g. <c>WORKFLOW.TASK.ASSIGNED</c>)
/// to one of the supported MNotify channels and carries the editable subject +
/// body content surfaced to the citizen by the MNotify gateway.
/// </summary>
/// <remarks>
/// <para>
/// <b>Channel kind.</b> A single template targets exactly one channel
/// (<see cref="MNotifyChannelKind"/>); cross-channel campaigns produce multiple
/// rows sharing the same logical event but distinct codes (e.g.
/// <c>WORKFLOW.TASK.ASSIGNED.EMAIL</c> vs. <c>WORKFLOW.TASK.ASSIGNED.SMS</c>).
/// </para>
/// <para>
/// <b>Soft delete.</b> Deactivating a template flips
/// <see cref="AuditableEntity.IsActive"/> to <c>false</c> so the registry pick
/// list hides the row while the historical references stay intact.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/>; admins reference
/// templates by Sqid on the <c>/api/admin/mnotify/templates</c> surface. The
/// natural-key Code is also unique and is the authoritative business key.
/// </para>
/// </remarks>
public sealed class MNotifyTemplate : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable SCREAMING_SNAKE_CASE template code. Pattern
    /// <c>^[A-Z][A-Z0-9_.]{1,79}$</c> — allows dotted namespace segments
    /// (e.g. <c>WORKFLOW.TASK.ASSIGNED</c>). Length ≤ 80. Unique within the
    /// system.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Channel this template targets — email / SMS / Viber / push.</summary>
    public MNotifyChannelKind ChannelKind { get; set; }

    /// <summary>
    /// One-line subject. Required for Email channels; ignored (but allowed) on
    /// SMS/Viber/Push. Validated by the application service before persist.
    /// Capped at 256 characters.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// Markdown body of the message. Required for every channel; capped at
    /// <see cref="MaxBodyLength"/> characters. The MNotify gateway renders the
    /// markdown into the channel-appropriate payload (HTML for email, plain
    /// text + line breaks for SMS).
    /// </summary>
    public string BodyMarkdown { get; set; } = string.Empty;

    /// <summary>
    /// Internal user id of the operator that most recently mutated this row.
    /// Captured at every <c>Upsert</c> call alongside <see cref="AuditableEntity.UpdatedBy"/>.
    /// </summary>
    public long? UpdatedByUserId { get; set; }

    /// <summary>Maximum length of <see cref="BodyMarkdown"/> in characters (16 KiB).</summary>
    public const int MaxBodyLength = 16_384;

    /// <summary>Maximum length of <see cref="Code"/> in characters.</summary>
    public const int MaxCodeLength = 80;

    /// <summary>Maximum length of <see cref="Subject"/> in characters.</summary>
    public const int MaxSubjectLength = 256;
}

/// <summary>
/// R0115 / TOR CF 14.07 — closed vocabulary of MNotify dispatch channels supported
/// by the MEGA <c>POST /api/Notification</c> surface. Persisted as the stable enum
/// name string on <see cref="MNotifyTemplate.ChannelKind"/>.
/// </summary>
public enum MNotifyChannelKind
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
