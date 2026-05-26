namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2500 / TOR PIR 020-023 — single SLA-related event attached to a
/// <see cref="SupportTicket"/>. Inserted by
/// <c>ISupportTicketSlaEvaluator</c> on each sweep when a new event becomes
/// detectable (a breach, a target met, or a manual / auto escalation).
/// </summary>
/// <remarks>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> — events cross
/// the API boundary as Sqid strings on the ticket detail DTO.
/// </para>
/// <para>
/// <b>Idempotency.</b> The evaluator dedupes on <c>(TicketId, EventKind)</c>:
/// a second sweep over a ticket that has already accumulated a
/// <c>FirstResponseBreached</c> row is a no-op. The deduplication is the
/// reason re-running the Quartz fire on the same data set is safe.
/// </para>
/// <para>
/// <b>PII discipline.</b> <see cref="Notes"/> is bounded to 1000 characters
/// and reserved for evaluator-supplied, PII-free annotations (e.g. the
/// <c>EventKind</c> name plus the breach delta). Operator-typed text never
/// lands here — that goes to <see cref="SupportTicketComment"/>.
/// </para>
/// </remarks>
public sealed class SupportTicketSlaEvent : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="SupportTicket"/>.</summary>
    public long TicketId { get; set; }

    /// <summary>Kind of SLA event recorded (see <see cref="SupportTicketSlaEventKind"/>).</summary>
    public SupportTicketSlaEventKind EventKind { get; set; }

    /// <summary>UTC instant the evaluator detected the event.</summary>
    public DateTime DetectedAt { get; set; }

    /// <summary>
    /// Optional PII-free annotation (≤ 1000 chars) — the evaluator writes a
    /// short stable string (e.g. <c>"Auto-escalated"</c>); operator-typed
    /// text never lands here.
    /// </summary>
    public string? Notes { get; set; }
}
