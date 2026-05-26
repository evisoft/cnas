namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2500 / TOR PIR 020-023 — operator-configurable category registry for
/// <see cref="SupportTicket"/> rows. Each row binds a stable SCREAMING_SNAKE_CASE
/// <see cref="Code"/> to a default severity, per-category SLA targets
/// (first-response + resolution), and the symbolic escalation-queue identifier
/// the auto-escalation path stamps on breached tickets.
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural-key uniqueness.</b> <see cref="Code"/> is the stable identifier
/// (e.g. <c>AUTH</c>, <c>PAYMENT</c>, <c>MIGRATION</c>, <c>BUG</c>,
/// <c>OUTAGE</c>); EF enforces a unique constraint. Tickets reference the
/// category by FK (<see cref="SupportTicket.CategoryId"/>) and denormalise the
/// code on outbound DTOs so consumers don't need to round-trip the catalog.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> — operators
/// reference categories by Sqid through the admin REST surface.
/// </para>
/// <para>
/// <b>SLA windows.</b> The two SLA fields are minute-grade so the helpdesk
/// can express both the 5-minute Critical first-response target (PIR 020) and
/// the 3-day Low resolution target (PIR 023) inside the same column. Bounds
/// are checked at the validator (5..7200 min for first-response, 30..43200
/// min for resolution).
/// </para>
/// <para>
/// <b>Escalation queue.</b> <see cref="EscalationQueueCode"/> is a loose
/// symbolic identifier (no FK) so operators can integrate with whichever
/// downstream queue (PagerDuty, in-app inbox, e-mail group, ...) they wire up.
/// </para>
/// </remarks>
public sealed class SupportTicketCategory : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable SCREAMING_SNAKE_CASE category code (e.g. <c>AUTH</c>,
    /// <c>PAYMENT</c>, <c>OUTAGE</c>). Pattern <c>^[A-Z][A-Z0-9_]{1,63}$</c>,
    /// length ≤ 64. Unique within the system.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable display name. Bounded to 256 characters.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// R0027 / TOR ARH 022 — optional Romanian display name. When null the
    /// <c>ILocalizedNameResolver</c> falls back to <see cref="DisplayName"/>;
    /// when populated it overrides <see cref="DisplayName"/> for the <c>"ro"</c>
    /// culture. Capped at 256 chars at the persistence layer.
    /// </summary>
    public string? NameRo { get; set; }

    /// <summary>
    /// R0027 / TOR ARH 022 — optional Russian display name. Capped at 256 chars
    /// at the persistence layer.
    /// </summary>
    public string? NameRu { get; set; }

    /// <summary>
    /// R0027 / TOR ARH 022 — optional English display name. Capped at 256 chars
    /// at the persistence layer.
    /// </summary>
    public string? NameEn { get; set; }

    /// <summary>Optional free-form description. Bounded to 1000 characters.</summary>
    public string? Description { get; set; }

    /// <summary>Default severity applied to tickets opened under this category.</summary>
    public SupportTicketSeverity DefaultSeverity { get; set; } = SupportTicketSeverity.Normal;

    /// <summary>
    /// First-response SLA target in minutes (5..7200). Tickets that remain in
    /// <see cref="SupportTicketStatus.Submitted"/> past
    /// <c>SubmittedAt + FirstResponseSlaMinutes</c> are breach candidates for
    /// the SLA evaluator.
    /// </summary>
    public int FirstResponseSlaMinutes { get; set; }

    /// <summary>
    /// Resolution SLA target in minutes (30..43200). Tickets that have not
    /// reached <see cref="SupportTicketStatus.Resolved"/> /
    /// <see cref="SupportTicketStatus.Closed"/> past
    /// <c>SubmittedAt + ResolutionSlaMinutes</c> are breach candidates.
    /// </summary>
    public int ResolutionSlaMinutes { get; set; }

    /// <summary>
    /// Symbolic queue identifier used at auto-escalation time. Validated
    /// against the same pattern as <see cref="Code"/> at the API boundary;
    /// no FK — the helpdesk treats this as an opaque routing token.
    /// Bounded to 64 characters.
    /// </summary>
    public string EscalationQueueCode { get; set; } = string.Empty;

    /// <summary>Raw <c>UserProfile.Id</c> of the admin who registered the category.</summary>
    public long RegisteredByUserId { get; set; }
}
