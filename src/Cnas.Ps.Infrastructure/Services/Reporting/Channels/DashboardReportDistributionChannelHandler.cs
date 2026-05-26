using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services.Reporting.Channels;

/// <summary>
/// R1906 / TOR Annex 6 — operational-dashboard surface handler. Same
/// best-effort semantics as the in-system inbox handler: the dispatch row
/// is the persistent record that the report ran for the recipient; the
/// physical dashboard tile renders straight from that row at refresh time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate channel.</b> The dashboard widget surface is distinct
/// from the in-system notification inbox even though both physically read
/// the same data store. Operators may want a report to surface as a
/// dashboard tile WITHOUT also queueing an inbox row — the channel choice
/// drives that selection at fan-out time.
/// </para>
/// </remarks>
public sealed class DashboardReportDistributionChannelHandler : IReportDistributionChannelHandler
{
    private readonly ICnasTimeProvider _clock;

    /// <summary>Constructs the handler.</summary>
    /// <param name="clock">UTC clock abstraction.</param>
    public DashboardReportDistributionChannelHandler(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <inheritdoc />
    public ReportDistributionChannel Channel => ReportDistributionChannel.Dashboard;

    /// <inheritdoc />
    public Task<ReportChannelDeliveryOutcome> DispatchAsync(
        ReportDistributionRule rule,
        ReportDispatchInputDto input,
        string resolvedRecipientAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        // Read the clock so the handler honours the injected
        // ICnasTimeProvider abstraction even when there is no row write —
        // future iterations may persist a dashboard-tile row at dispatch
        // time and the clock dependency is then already in place.
        _ = _clock.UtcNow;

        return Task.FromResult(new ReportChannelDeliveryOutcome(
            Status: ReportDispatchStatus.Delivered,
            FailureReason: null));
    }
}
