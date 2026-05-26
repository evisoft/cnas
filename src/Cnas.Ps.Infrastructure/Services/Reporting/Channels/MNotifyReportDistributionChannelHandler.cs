using System.Collections.Generic;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services.Reporting.Channels;

/// <summary>
/// R1906 / TOR Annex 6 — MNotify channel handler. Delegates to the wired
/// <see cref="IMNotifyClient"/>. Translates transport failures into
/// <see cref="ReportDispatchStatus.Failed"/> with a sanitised reason; never
/// throws.
/// </summary>
/// <remarks>
/// <para>
/// <b>Multi-language envelope.</b> The handler emits a minimal RO/EN/RU
/// notification body composed of the report title + summary + deep link.
/// MNotify's typed-recipient model accepts the address verbatim — for the
/// email-routed kind we use <c>NotificationRecipientType.Email</c>, for
/// MNotify categories we still pass the value through email-shaped
/// addressing (the dispatcher treats MNotify category fan-out as the
/// upstream concern; the rule's recipient code is forwarded to MNotify
/// which performs its own fan-out).
/// </para>
/// <para>
/// <b>No PII in failure reasons.</b> The handler catches every exception
/// from the client and records only the exception type name as the
/// failure reason. The recipient address is NEVER logged.
/// </para>
/// </remarks>
public sealed class MNotifyReportDistributionChannelHandler : IReportDistributionChannelHandler
{
    private readonly IMNotifyClient? _mnotify;

    /// <summary>Stable failure-reason code surfaced when no MNotify client is configured.</summary>
    public const string ReasonNoMNotifyClientConfigured = "NO_MNOTIFY_CLIENT_CONFIGURED";

    /// <summary>Constructs the handler with the optional MNotify client.</summary>
    /// <param name="mnotify">
    /// Optional MNotify client. <c>null</c> in test fixtures that elect not
    /// to wire transport; the handler then returns <c>Skipped</c> rather than
    /// throwing.
    /// </param>
    public MNotifyReportDistributionChannelHandler(IMNotifyClient? mnotify = null)
    {
        _mnotify = mnotify;
    }

    /// <inheritdoc />
    public ReportDistributionChannel Channel => ReportDistributionChannel.MNotify;

    /// <inheritdoc />
    public async Task<ReportChannelDeliveryOutcome> DispatchAsync(
        ReportDistributionRule rule,
        ReportDispatchInputDto input,
        string resolvedRecipientAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(input);

        if (_mnotify is null)
        {
            return new ReportChannelDeliveryOutcome(
                Status: ReportDispatchStatus.Skipped,
                FailureReason: ReasonNoMNotifyClientConfigured);
        }

        try
        {
            // Map the resolved address to a typed MNotify recipient. We bias
            // toward Email because the rule's recipient kind determines the
            // outer fan-out; on this leaf we always have a usable address.
            var recipient = new NotificationRecipient(
                NotificationRecipientType.Email,
                resolvedRecipientAddress);

            var subject = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ro"] = input.ReportTitle,
                ["en"] = input.ReportTitle,
                ["ru"] = input.ReportTitle,
            };
            var body = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ro"] = $"{input.ReportSummary}\n{input.PayloadDownloadUrl}",
                ["en"] = $"{input.ReportSummary}\n{input.PayloadDownloadUrl}",
                ["ru"] = $"{input.ReportSummary}\n{input.PayloadDownloadUrl}",
            };

            var request = new NotificationRequest(
                Subject: subject,
                Body: body,
                BodyShort: null,
                Recipients: new List<NotificationRecipient> { recipient },
                Attachments: null,
                CorrelationId: input.ReportRunSqid);

            var dispatch = await _mnotify
                .SendNotificationAsync(request, cancellationToken)
                .ConfigureAwait(false);

            return dispatch.IsSuccess
                ? new ReportChannelDeliveryOutcome(ReportDispatchStatus.Delivered, null)
                : new ReportChannelDeliveryOutcome(
                    ReportDispatchStatus.Failed,
                    FailureReason: dispatch.ErrorCode ?? "MNOTIFY_FAILED");
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation — the dispatcher unwinds the loop.
            throw;
        }
        catch (Exception ex)
        {
            // Sanitised reason — type-name only, no PII / message text.
            return new ReportChannelDeliveryOutcome(
                Status: ReportDispatchStatus.Failed,
                FailureReason: $"MNOTIFY.{ex.GetType().Name}");
        }
    }
}
