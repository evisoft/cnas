using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Tests.Reporting;

/// <summary>
/// R1906 / TOR Annex 6 — tests for <see cref="ReportDistributionDispatcher"/>.
/// Verifies the happy path persists Delivered rows + emits the outcome
/// counters, the no-matching-rules path returns an empty summary, a
/// throwing handler is contained and recorded as Failed without PII leak,
/// and a format mismatch resolves to Skipped.
/// </summary>
public sealed class ReportDistributionDispatcherTests
{
    private static ReportDistributionDispatcher NewDispatcher(
        CnasDbContext db,
        IEnumerable<IReportDistributionChannelHandler> handlers,
        IReportRecipientResolver? resolverOverride = null)
    {
        var resolver = resolverOverride
            ?? new ReportRecipientResolver(db, ReportDistributionTestHelpers.NewSqidMock());
        return new ReportDistributionDispatcher(
            db: db,
            clock: new ReportDistributionTestHelpers.StubClock(ReportDistributionTestHelpers.ClockNow),
            caller: ReportDistributionTestHelpers.NewCaller(),
            resolver: resolver,
            handlers: handlers,
            validator: new ReportDispatchInputValidator());
    }

    private static ReportDistributionRule MakeRule(
        string reportCode = "ACCESS_RIGHTS.FULL_MATRIX",
        ReportDistributionChannel channel = ReportDistributionChannel.InSystem,
        ReportRecipientKind recipientKind = ReportRecipientKind.EmailAddress,
        string recipientCode = "ops@example.org",
        ReportDeliveryFormat format = ReportDeliveryFormat.Pdf)
        => new()
        {
            ReportCode = reportCode,
            Channel = channel,
            RecipientKind = recipientKind,
            RecipientCode = recipientCode,
            RecipientCodeHash = "HASH:" + recipientCode.ToUpperInvariant(),
            Format = format,
            Priority = ReportDeliveryPriority.Normal,
            EffectiveFrom = new DateOnly(2026, 1, 1),
            EffectiveUntil = null,
            CreatedAtUtc = ReportDistributionTestHelpers.ClockNow,
            CreatedBy = "test",
            IsActive = true,
        };

    private static ReportDispatchInputDto MakeInput(
        string reportCode = "ACCESS_RIGHTS.FULL_MATRIX",
        string format = "Pdf",
        string runSqid = "RUN-1")
        => new(
            ReportCode: reportCode,
            ReportRunSqid: runSqid,
            Format: format,
            ReportTitle: "Access Rights Full Matrix",
            ReportSummary: "Quarterly access-rights snapshot.",
            PayloadDownloadUrl: "https://cnas.local/reports/x",
            PayloadSize: 12345,
            EvaluatedAt: ReportDistributionTestHelpers.ClockNow);

    private sealed class StubHandler(
        ReportDistributionChannel channel,
        ReportDispatchStatus terminalStatus,
        string? failureReason = null,
        Exception? toThrow = null) : IReportDistributionChannelHandler
    {
        public ReportDistributionChannel Channel { get; } = channel;

        public Task<ReportChannelDeliveryOutcome> DispatchAsync(
            ReportDistributionRule rule,
            ReportDispatchInputDto input,
            string resolvedRecipientAddress,
            CancellationToken cancellationToken = default)
        {
            if (toThrow is not null)
            {
                throw toThrow;
            }
            return Task.FromResult(new ReportChannelDeliveryOutcome(terminalStatus, failureReason));
        }
    }

    [Fact]
    public async Task DispatchAsync_HappyPath_PersistsDeliveredAndEmitsCounters()
    {
        using var db = ReportDistributionTestHelpers.CreateContext();
        db.ReportDistributionRules.Add(MakeRule());
        await db.SaveChangesAsync();

        var handlers = new[]
        {
            new StubHandler(ReportDistributionChannel.InSystem, ReportDispatchStatus.Delivered),
        };

        var counterTotal = 0L;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == CnasMeter.MeterName
                    && instrument.Name == "cnas.report_distribution.dispatch_outcome")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, m, _, _) => Interlocked.Add(ref counterTotal, m));
        listener.Start();

        var dispatcher = NewDispatcher(db, handlers);
        var result = await dispatcher.DispatchAsync(MakeInput());

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalRules.Should().Be(1);
        result.Value.Delivered.Should().Be(1);
        result.Value.Failed.Should().Be(0);
        result.Value.Skipped.Should().Be(0);
        counterTotal.Should().Be(1);
        var rows = await db.ReportDistributionDispatches.ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].Status.Should().Be(ReportDispatchStatus.Delivered);
    }

    [Fact]
    public async Task DispatchAsync_NoActiveRules_ReturnsZeroSummary()
    {
        using var db = ReportDistributionTestHelpers.CreateContext();
        var handlers = new[]
        {
            new StubHandler(ReportDistributionChannel.InSystem, ReportDispatchStatus.Delivered),
        };
        var dispatcher = NewDispatcher(db, handlers);

        var result = await dispatcher.DispatchAsync(MakeInput());

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalRules.Should().Be(0);
        result.Value.Delivered.Should().Be(0);
        result.Value.Failed.Should().Be(0);
        result.Value.Skipped.Should().Be(0);
        (await db.ReportDistributionDispatches.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_HandlerThrows_RecordsFailedWithSanitisedReason()
    {
        using var db = ReportDistributionTestHelpers.CreateContext();
        db.ReportDistributionRules.Add(MakeRule());
        await db.SaveChangesAsync();

        var handlers = new[]
        {
            new StubHandler(
                ReportDistributionChannel.InSystem,
                ReportDispatchStatus.Delivered,
                toThrow: new InvalidOperationException("ops@secret.org rejected")),
        };
        var dispatcher = NewDispatcher(db, handlers);

        var result = await dispatcher.DispatchAsync(MakeInput());

        result.IsSuccess.Should().BeTrue();
        result.Value.Failed.Should().Be(1);
        var rows = await db.ReportDistributionDispatches.ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].Status.Should().Be(ReportDispatchStatus.Failed);
        rows[0].FailureReason.Should().Be("DISPATCH.InvalidOperationException");
        rows[0].FailureReason.Should().NotContain("ops@secret.org");
        rows[0].FailureReason.Should().NotContain("rejected");
    }

    [Fact]
    public async Task DispatchAsync_FormatMismatch_RecordsSkipped()
    {
        using var db = ReportDistributionTestHelpers.CreateContext();
        db.ReportDistributionRules.Add(MakeRule(format: ReportDeliveryFormat.Pdf));
        await db.SaveChangesAsync();

        var handlers = new[]
        {
            new StubHandler(ReportDistributionChannel.InSystem, ReportDispatchStatus.Delivered),
        };
        var dispatcher = NewDispatcher(db, handlers);

        // Caller asks for CSV; rule expects PDF — outcome should be Skipped.
        var result = await dispatcher.DispatchAsync(MakeInput(format: "Csv"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Skipped.Should().Be(1);
        result.Value.Delivered.Should().Be(0);
        var rows = await db.ReportDistributionDispatches.ToListAsync();
        rows[0].Status.Should().Be(ReportDispatchStatus.Skipped);
        rows[0].FailureReason.Should().Be("FORMAT_MISMATCH");
    }
}
