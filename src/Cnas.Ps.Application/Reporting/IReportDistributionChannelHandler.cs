using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Reporting;

/// <summary>
/// R1906 / TOR Annex 6 — per-channel dispatcher consulted by
/// <see cref="IReportDistributionDispatcher"/>. Implementations encapsulate
/// the transport-specific delivery logic for ONE
/// <see cref="ReportDistributionChannel"/> value (in-system inbox,
/// dashboard surface, email, MNotify). The orchestrator selects the
/// implementation by matching <see cref="Channel"/> against the rule.
/// </summary>
/// <remarks>
/// <para>
/// <b>Handler contract.</b> A handler MUST NEVER throw. Transport
/// exceptions (network failures, missing configuration, upstream rejection)
/// are caught internally and surfaced through the
/// <see cref="ReportChannelDeliveryOutcome"/> shape. The dispatcher relies
/// on this contract to keep the fan-out bounded — a misbehaving channel
/// must not poison the rest of the dispatch loop.
/// </para>
/// <para>
/// <b>No PII in <c>FailureReason</c>.</b> Implementations must produce
/// sanitised failure strings — channel-name + stable failure code, never
/// the recipient address or the report payload. The dispatcher persists
/// the string verbatim on the dispatch row.
/// </para>
/// </remarks>
public interface IReportDistributionChannelHandler
{
    /// <summary>Which channel value this handler covers — used by the dispatcher to select the right implementation.</summary>
    ReportDistributionChannel Channel { get; }

    /// <summary>
    /// Delivers the report run to ONE recipient (the rule's resolved
    /// recipient) through this handler's channel. Returns the outcome
    /// envelope; never throws.
    /// </summary>
    /// <param name="rule">The matched <see cref="ReportDistributionRule"/>.</param>
    /// <param name="input">The dispatch envelope describing the report run.</param>
    /// <param name="resolvedRecipientAddress">
    /// The post-resolver recipient address (e.g. a single email, a single user's
    /// MNotify subject, a group code). The handler treats this as opaque text and
    /// hands it to the transport client.
    /// </param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The terminal outcome envelope.</returns>
    Task<ReportChannelDeliveryOutcome> DispatchAsync(
        ReportDistributionRule rule,
        ReportDispatchInputDto input,
        string resolvedRecipientAddress,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// R1906 / TOR Annex 6 — terminal outcome of one
/// <see cref="IReportDistributionChannelHandler.DispatchAsync"/> call.
/// </summary>
/// <param name="Status">Terminal status — <c>Delivered</c> / <c>Failed</c> / <c>Skipped</c>.</param>
/// <param name="FailureReason">
/// Sanitised reason for the non-success branches; null on <c>Delivered</c>.
/// MUST NEVER contain PII.
/// </param>
public sealed record ReportChannelDeliveryOutcome(
    ReportDispatchStatus Status,
    string? FailureReason);
