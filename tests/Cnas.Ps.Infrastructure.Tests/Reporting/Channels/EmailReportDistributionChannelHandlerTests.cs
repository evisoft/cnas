using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Reporting.Channels;
using Cnas.Ps.Infrastructure.Tests.Reporting;

namespace Cnas.Ps.Infrastructure.Tests.Reporting.Channels;

/// <summary>
/// R1906 / TOR Annex 6 — tests for
/// <see cref="EmailReportDistributionChannelHandler"/>. The current
/// codebase does not wire an <c>IEmailSender</c> abstraction (production
/// emails flow through MNotify), so the handler returns Skipped with the
/// stable <c>NO_EMAIL_SENDER_CONFIGURED</c> reason. These tests pin the
/// fall-back contract so a future iteration wiring email transport must
/// update both the handler and its test fixtures together.
/// </summary>
public sealed class EmailReportDistributionChannelHandlerTests
{
    private static ReportDistributionRule MakeRule(string recipientCode = "ops@example.org") => new()
    {
        ReportCode = "ACCESS_RIGHTS.FULL_MATRIX",
        Channel = ReportDistributionChannel.Email,
        RecipientKind = ReportRecipientKind.EmailAddress,
        RecipientCode = recipientCode,
        Format = ReportDeliveryFormat.Pdf,
        Priority = ReportDeliveryPriority.Normal,
        EffectiveFrom = new DateOnly(2026, 1, 1),
        CreatedAtUtc = ReportDistributionTestHelpers.ClockNow,
    };

    private static ReportDispatchInputDto MakeInput() => new(
        ReportCode: "ACCESS_RIGHTS.FULL_MATRIX",
        ReportRunSqid: "RUN-1",
        Format: "Pdf",
        ReportTitle: "Access Rights Full Matrix",
        ReportSummary: "Quarterly snapshot.",
        PayloadDownloadUrl: "https://cnas.local/r/1",
        PayloadSize: 1024,
        EvaluatedAt: ReportDistributionTestHelpers.ClockNow);

    [Fact]
    public async Task DispatchAsync_NoEmailSenderConfigured_ReturnsSkippedWithStableReason()
    {
        var handler = new EmailReportDistributionChannelHandler();

        var outcome = await handler.DispatchAsync(MakeRule(), MakeInput(), "ops@example.org");

        outcome.Status.Should().Be(ReportDispatchStatus.Skipped);
        outcome.FailureReason.Should().Be(EmailReportDistributionChannelHandler.ReasonNoEmailSenderConfigured);
    }

    [Fact]
    public async Task DispatchAsync_FailureReason_DoesNotLeakRecipientAddress()
    {
        var handler = new EmailReportDistributionChannelHandler();

        var outcome = await handler.DispatchAsync(MakeRule(), MakeInput(), "secret-leak@example.org");

        outcome.FailureReason.Should().NotContain("secret-leak");
        outcome.FailureReason.Should().NotContain("@example.org");
    }
}
