using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services.Reporting.Channels;

/// <summary>
/// R1906 / TOR Annex 6 — email channel handler. Returns
/// <see cref="ReportDispatchStatus.Skipped"/> with reason
/// <c>NO_EMAIL_SENDER_CONFIGURED</c> when the codebase does not have a
/// dedicated <c>IEmailSender</c> wired (the current state — emails are
/// dispatched via MNotify in production). The handler stays in place as a
/// future expansion seam.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not throw.</b> A missing transport is a configuration choice, not
/// a programming error. Channel handlers are forbidden from throwing
/// (CLAUDE.md rejection rule 10) — the dispatch row records the
/// <c>Skipped</c> outcome so operators see the consequence of the missing
/// configuration without crashing the fan-out loop.
/// </para>
/// <para>
/// <b>No PII in failure reasons.</b> The <c>NO_EMAIL_SENDER_CONFIGURED</c>
/// string is a stable code; the recipient address is NEVER logged.
/// </para>
/// </remarks>
public sealed class EmailReportDistributionChannelHandler : IReportDistributionChannelHandler
{
    /// <summary>
    /// Stable failure-reason code surfaced when no email sender is configured.
    /// Persisted verbatim on the dispatch row so operators can chart the
    /// missing-transport count.
    /// </summary>
    public const string ReasonNoEmailSenderConfigured = "NO_EMAIL_SENDER_CONFIGURED";

    /// <summary>Constructs the handler. The email-sender abstraction is not yet wired in the codebase.</summary>
    public EmailReportDistributionChannelHandler()
    {
    }

    /// <inheritdoc />
    public ReportDistributionChannel Channel => ReportDistributionChannel.Email;

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

        // The infrastructure does not currently wire an IEmailSender abstraction
        // (production emails flow through MNotify). Return Skipped with the
        // stable sentinel reason so operators can chart the consequence of the
        // missing transport without us throwing.
        return Task.FromResult(new ReportChannelDeliveryOutcome(
            Status: ReportDispatchStatus.Skipped,
            FailureReason: ReasonNoEmailSenderConfigured));
    }
}
