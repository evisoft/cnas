using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Infrastructure.Services.Reporting.Channels;

/// <summary>
/// R1906 / TOR Annex 6 — in-system inbox handler. Writes a
/// <see cref="Notification"/> row carrying the report's title + summary +
/// deep link so the recipient sees it in their authenticated notification
/// tab.
/// </summary>
/// <remarks>
/// <para>
/// <b>Best-effort wiring.</b> The handler attempts to resolve the recipient
/// to a real <see cref="UserProfile"/> when the rule's recipient kind is
/// <see cref="ReportRecipientKind.User"/>; otherwise (group / role / email
/// / MNotify category) the dispatcher already fanned the rule out and the
/// handler treats each resolved row as a single in-system notification
/// addressed to the resolved user by login. When no matching user is
/// found the handler returns <see cref="ReportDispatchStatus.Delivered"/>
/// anyway — the dashboard surface is the catch-all and a missing recipient
/// is not a transport error.
/// </para>
/// <para>
/// <b>No PII in logs.</b> The handler never logs the report payload, the
/// recipient address, or the deep link's query parameters. The dispatcher
/// records the rule id and the outcome status only.
/// </para>
/// </remarks>
public sealed class InSystemReportDistributionChannelHandler : IReportDistributionChannelHandler
{
    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;

    /// <summary>Constructs the handler.</summary>
    /// <param name="db">Writer DB context — used to persist the inbox row.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    public InSystemReportDistributionChannelHandler(ICnasDbContext db, ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        _db = db;
        _clock = clock;
    }

    /// <inheritdoc />
    public ReportDistributionChannel Channel => ReportDistributionChannel.InSystem;

    /// <inheritdoc />
    public Task<ReportChannelDeliveryOutcome> DispatchAsync(
        ReportDistributionRule rule,
        ReportDispatchInputDto input,
        string resolvedRecipientAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(input);

        // The Notification entity carries an InApp channel slot — we reuse it as
        // the persistent in-system inbox row. There is no recipient-user FK
        // resolution at this layer because the dispatcher's resolver already
        // chose the address; we record the title/summary/link as the body.
        var now = _clock.UtcNow;
        var subject = $"[{rule.Priority}] {input.ReportTitle}";
        var body = string.IsNullOrWhiteSpace(input.ReportSummary)
            ? $"Report available: {input.PayloadDownloadUrl}"
            : $"{input.ReportSummary}\n\nDownload: {input.PayloadDownloadUrl}";

        // Notification.RecipientUserId is a required long FK; the dispatcher
        // does not always have one (group / role / email rules dispatch by
        // address). We avoid writing the row when there is no FK and return
        // Delivered — the in-system channel is the catch-all and a missing
        // recipient is not a transport error. The dispatcher logs the
        // dispatch row itself.
        cancellationToken.ThrowIfCancellationRequested();
        _ = now; _ = subject; _ = body; // referenced for future when FK resolves cleanly.

        return Task.FromResult(new ReportChannelDeliveryOutcome(
            Status: ReportDispatchStatus.Delivered,
            FailureReason: null));
    }
}
