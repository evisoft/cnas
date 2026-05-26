namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2500 / TOR PIR 020-023 — single comment attached to a
/// <see cref="SupportTicket"/>. Append-only; the helpdesk treats the comment
/// timeline as the audit narrative for the ticket.
/// </summary>
/// <remarks>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> — comments cross
/// the API boundary as Sqid strings on the ticket detail DTO.
/// </para>
/// <para>
/// <b>Confidential payload.</b> <see cref="Body"/> may carry PII supplied by
/// the requester or operator. The contracts layer labels it
/// <c>Confidential</c>; the system NEVER logs the body raw — audit events
/// emitted on <c>AddCommentAsync</c> reference only the ticket sqid and the
/// comment author.
/// </para>
/// <para>
/// <b>Visibility.</b> <see cref="IsInternalOnly"/> is the operator-only
/// flag — requester-side surfaces filter out internal comments before
/// rendering.
/// </para>
/// </remarks>
public sealed class SupportTicketComment : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="SupportTicket"/>.</summary>
    public long TicketId { get; set; }

    /// <summary>Raw <c>UserProfile.Id</c> of the comment author.</summary>
    public long AuthorUserId { get; set; }

    /// <summary>Comment body (3..8000 chars). Treated <c>Confidential</c> at egress.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// True when the comment is visible to operators only. Defaults to
    /// <c>false</c>; requester-side surfaces filter on this flag.
    /// </summary>
    public bool IsInternalOnly { get; set; }

    /// <summary>UTC instant the comment was posted.</summary>
    public DateTime PostedAt { get; set; }
}
