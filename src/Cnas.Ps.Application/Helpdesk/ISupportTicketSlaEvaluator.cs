using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Helpdesk;

/// <summary>
/// R2500 / TOR PIR 020-023 — periodic SLA evaluator. Iterates non-terminal
/// helpdesk tickets, classifies each against its computed
/// <c>FirstResponseDueAt</c> / <c>ResolutionDueAt</c> deadlines, and inserts
/// a <c>SupportTicketSlaEvent</c> row for every newly-detected SLA event.
/// On <c>FirstResponseBreached</c> / <c>ResolutionBreached</c> the ticket is
/// AUTO-ESCALATED: Status → <c>SupportTicketStatus.Escalated</c>, EscalatedAt
/// stamped to now, EscalationReason set to <c>"Auto-escalated due to {EventKind}"</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency.</b> The evaluator dedupes on <c>(TicketId, EventKind)</c>:
/// re-running the sweep on the same row set is a no-op. Re-recording the
/// same SLA event for the same ticket is a bug per the discipline rules.
/// </para>
/// <para>
/// <b>Cadence.</b> Wired into the Quartz scheduler by
/// <c>SupportTicketSlaEvaluationJob</c> at <c>0 *&#47;5 * * * ?</c> (every 5
/// minutes). The job registers under the <c>Always</c> peak-hour profile —
/// SLA enforcement is 24/7.
/// </para>
/// <para>
/// <b>Audit + metrics.</b> Each detected breach emits a Critical
/// <c>TICKET.SLA.BREACHED</c> audit row + increments the
/// <c>cnas.support_ticket.sla_breached</c> counter (tagged with
/// <c>category_code</c> + <c>event_kind</c>). Auto-escalations also bump
/// <c>cnas.support_ticket.auto_escalated</c>.
/// </para>
/// </remarks>
public interface ISupportTicketSlaEvaluator
{
    /// <summary>Stable audit event code emitted on breach detection.</summary>
    public const string AuditSlaBreached = "TICKET.SLA.BREACHED";

    /// <summary>Stable audit event code emitted on auto-escalation.</summary>
    public const string AuditAutoEscalated = "TICKET.AUTO_ESCALATED";

    /// <summary>
    /// Sweeps non-terminal tickets, records newly-detected SLA events, and
    /// auto-escalates rows whose first-response or resolution deadlines have
    /// elapsed. Idempotent — a re-fire on the same row set is a no-op.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of SLA events newly persisted.</returns>
    Task<Result<int>> EvaluateAsync(CancellationToken cancellationToken = default);
}
