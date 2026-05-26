using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Jobs;

/// <summary>
/// R0818 / TOR BP 1.2-I — tests for <see cref="DailyBassReceiptsSummaryJob"/>.
/// Verifies the job emits the BASS_RECEIPTS.DAILY_SUMMARY Information-severity
/// audit row containing the per-status counts for the current operating day.
/// </summary>
public sealed class DailyBassReceiptsSummaryJobTests
{
    /// <summary>Fixed UTC clock used by every test (midday so the day-window is unambiguous).</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Stub clock returning the fixed instant.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-bass-daily-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Returns a no-op Quartz job-execution context.</summary>
    private static IJobExecutionContext FakeContext()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        ctx.FireInstanceId.Returns("fire-bass-summary");
        return ctx;
    }

    /// <summary>Seeds a TreasuryPaymentReceipt distributed within the operating day.</summary>
    private static async Task SeedReceiptAsync(
        CnasDbContext db,
        TreasuryPaymentDistributionStatus status,
        DateTime distributedAt)
    {
        db.TreasuryPaymentReceipts.Add(new TreasuryPaymentReceipt
        {
            TreasuryReferenceNumber = $"TRS-{Guid.NewGuid():N}",
            ReceiptDate = DateOnly.FromDateTime(distributedAt),
            PayerContributorId = 1L,
            ReportingMonth = new DateOnly(2026, 4, 1),
            AmountReceived = 500m,
            DistributionStatus = status,
            DistributedAtUtc = distributedAt,
            CreatedAtUtc = distributedAt,
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>R0818 — fires the BASS_RECEIPTS.DAILY_SUMMARY Information-severity audit row.</summary>
    [Fact]
    public async Task Execute_WritesInformationAuditWithDailySummary()
    {
        using var db = CreateContext();
        var dayStart = new DateTime(ClockNow.Year, ClockNow.Month, ClockNow.Day, 0, 0, 0, DateTimeKind.Utc);
        await SeedReceiptAsync(db, TreasuryPaymentDistributionStatus.Distributed, dayStart.AddHours(2));
        await SeedReceiptAsync(db, TreasuryPaymentDistributionStatus.PartiallyDistributed, dayStart.AddHours(4));
        await SeedReceiptAsync(db, TreasuryPaymentDistributionStatus.Failed, dayStart.AddHours(6));
        // Yesterday's receipt — must be excluded by the day-window predicate.
        await SeedReceiptAsync(db, TreasuryPaymentDistributionStatus.Distributed, dayStart.AddDays(-1));

        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(),
                Arg.Any<AuditSeverity>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<long?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var job = new DailyBassReceiptsSummaryJob(
            db, new StubClock(ClockNow), audit,
            new Cnas.Ps.Infrastructure.Tests.Common.AllowAllPeakHourGate(),
            NullLogger<DailyBassReceiptsSummaryJob>.Instance);

        await job.Execute(FakeContext());

        await audit.Received(1).RecordAsync(
            DailyBassReceiptsSummaryJob.AuditEventCode,
            AuditSeverity.Information,
            DailyBassReceiptsSummaryJob.SystemActor,
            nameof(TreasuryPaymentReceipt),
            null,
            Arg.Is<string>(s => s.Contains("\"totalCount\":3") && s.Contains("\"distributed\":1")),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }
}
