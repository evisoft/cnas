namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2500 / TOR PIR 020-023 — helpdesk ticket aggregate. Carries the full
/// lifecycle of a user-submitted support request from
/// <see cref="SupportTicketStatus.Submitted"/> through
/// <see cref="SupportTicketStatus.Closed"/> / <see cref="SupportTicketStatus.Cancelled"/>,
/// with computed SLA due-dates and operator-supplied resolution / escalation
/// metadata.
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural-key uniqueness.</b> <see cref="TicketNumber"/> is the
/// deterministic <c>TKT-{year}-{seq:000000}</c> identifier — easy for
/// operators and requesters to quote in conversation. The EF configuration
/// enforces a unique constraint.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> — operators and
/// requesters reference tickets by Sqid through the REST surface.
/// </para>
/// <para>
/// <b>SLA windows.</b> <see cref="FirstResponseDueAt"/> and
/// <see cref="ResolutionDueAt"/> are computed at submit time from the
/// category's per-category SLA fields. The
/// <c>SupportTicketSlaEvaluator</c> sweeps non-terminal rows on a 5-minute
/// cadence and writes a <c>SupportTicketSlaEvent</c> row for every newly-
/// detected breach. Breach detection is idempotent — re-running on the same
/// row set is a no-op.
/// </para>
/// <para>
/// <b>Confidential payload.</b> <see cref="Description"/> may carry
/// user-supplied PII (account ids, error excerpts). The contracts layer
/// labels it <c>Confidential</c>; the system NEVER scrubs it. Audit events
/// emitted by the service layer never include the body — only the ticket
/// sqid + state transitions appear in audit / metrics.
/// </para>
/// </remarks>
public sealed class SupportTicket : AuditableEntity, IExternalId
{
    /// <summary>
    /// Deterministic <c>TKT-{year}-{seq:000000}</c> ticket number assigned at
    /// submit time. Bounded to 32 characters. Unique within the system.
    /// </summary>
    public string TicketNumber { get; set; } = string.Empty;

    /// <summary>FK to the <see cref="SupportTicketCategory"/> this ticket belongs to.</summary>
    public long CategoryId { get; set; }

    /// <summary>Short title (3..256 chars). Treated <c>Internal</c> at egress.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Free-form description (3..8000 chars). May carry PII supplied by the
    /// requester. Treated <c>Confidential</c> at egress; never logged raw.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Current severity. Defaulted from the category at submit time; can be
    /// elevated by an operator through <c>EscalateAsync</c>.
    /// </summary>
    public SupportTicketSeverity Severity { get; set; } = SupportTicketSeverity.Normal;

    /// <summary>Current lifecycle status (see <see cref="SupportTicketStatus"/>).</summary>
    public SupportTicketStatus Status { get; set; } = SupportTicketStatus.Submitted;

    /// <summary>Raw <c>UserProfile.Id</c> of the requester (the caller who submitted the ticket).</summary>
    public long SubmittedByUserId { get; set; }

    /// <summary>
    /// Raw <c>UserProfile.Id</c> of the operator the ticket is currently
    /// assigned to, or null while unassigned.
    /// </summary>
    public long? AssignedToUserId { get; set; }

    /// <summary>UTC instant the ticket was submitted.</summary>
    public DateTime SubmittedAt { get; set; }

    /// <summary>
    /// UTC instant of the first transition into
    /// <see cref="SupportTicketStatus.Acknowledged"/>. Stamped exactly once —
    /// later re-transitions do not overwrite. Stops the first-response SLA
    /// clock.
    /// </summary>
    public DateTime? FirstAcknowledgedAt { get; set; }

    /// <summary>UTC instant the ticket reached <see cref="SupportTicketStatus.Resolved"/>; null otherwise.</summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>UTC instant the ticket reached <see cref="SupportTicketStatus.Closed"/>; null otherwise.</summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary>
    /// Computed first-response deadline =
    /// <c>SubmittedAt + Category.FirstResponseSlaMinutes</c>. The SLA evaluator
    /// flags the ticket as <c>FirstResponseBreached</c> when this instant has
    /// passed and the ticket is still in
    /// <see cref="SupportTicketStatus.Submitted"/>.
    /// </summary>
    public DateTime FirstResponseDueAt { get; set; }

    /// <summary>
    /// Computed resolution deadline =
    /// <c>SubmittedAt + Category.ResolutionSlaMinutes</c>. The SLA evaluator
    /// flags the ticket as <c>ResolutionBreached</c> when this instant has
    /// passed and the ticket has not reached
    /// <see cref="SupportTicketStatus.Resolved"/> /
    /// <see cref="SupportTicketStatus.Closed"/>.
    /// </summary>
    public DateTime ResolutionDueAt { get; set; }

    /// <summary>UTC instant the ticket was escalated (auto or manual); null otherwise.</summary>
    public DateTime? EscalatedAt { get; set; }

    /// <summary>
    /// Operator-supplied escalation reason (3..500 chars). When the SLA
    /// evaluator auto-escalates, the reason is set to
    /// <c>Auto-escalated due to {EventKind}</c>.
    /// </summary>
    public string? EscalationReason { get; set; }

    /// <summary>
    /// Operator-supplied resolution summary (3..2000 chars) captured at the
    /// transition into <see cref="SupportTicketStatus.Resolved"/>.
    /// </summary>
    public string? ResolutionSummary { get; set; }

    /// <summary>Operator-supplied cancellation reason (3..500 chars) when the ticket is Cancelled.</summary>
    public string? CancelReason { get; set; }
}
