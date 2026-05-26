using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Treasury;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Jobs;

/// <summary>
/// R0911 / TOR BP 2.2-B — tests for <see cref="TreasuryDistributionJob"/>.
/// Verifies the per-fire drain path: every Pending row is handed to
/// <see cref="ITreasuryPaymentService.DistributeAsync"/> exactly once, and
/// the job no-ops when the backlog is empty.
/// </summary>
public sealed class TreasuryDistributionJobTests
{
    /// <summary>Fixed UTC clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-treasuryjob-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Returns a no-op Quartz job-execution context.</summary>
    private static IJobExecutionContext FakeContext()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        ctx.FireInstanceId.Returns("fire-test");
        return ctx;
    }

    /// <summary>Wires the InMemory DbContext + mock service into the scope factory.</summary>
    private static (TreasuryDistributionJob Job, ITreasuryPaymentService Service) Build(ICnasDbContext db)
    {
        var service = Substitute.For<ITreasuryPaymentService>();
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ICnasDbContext)).Returns(db);
        sp.GetService(typeof(ITreasuryPaymentService)).Returns(service);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var job = new TreasuryDistributionJob(
            scopeFactory,
            new Cnas.Ps.Infrastructure.Tests.Common.AllowAllPeakHourGate(),
            NullLogger<TreasuryDistributionJob>.Instance);
        return (job, service);
    }

    /// <summary>R0911 — empty Pending backlog → service is never invoked.</summary>
    [Fact]
    public async Task Execute_EmptyBacklog_NoOp()
    {
        using var db = CreateContext();
        var (job, service) = Build(db);

        await job.Execute(FakeContext());

        await service.DidNotReceive().DistributeAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    /// <summary>R0911 — Pending receipts are drained one DistributeAsync call each.</summary>
    [Fact]
    public async Task Execute_DrainsPendingReceipts()
    {
        using var db = CreateContext();
        // Seed three Pending receipts directly — we don't need the import path
        // here, the job's only contract is "drain everything in Pending".
        for (int i = 1; i <= 3; i++)
        {
            db.TreasuryPaymentReceipts.Add(new TreasuryPaymentReceipt
            {
                TreasuryReferenceNumber = $"TRS-JOB-{i:D3}",
                ReceiptDate = new DateOnly(2026, 5, i),
                PayerContributorId = 1L,
                ReportingMonth = new DateOnly(2026, 4, 1),
                AmountReceived = 100m * i,
                DistributionStatus = TreasuryPaymentDistributionStatus.Pending,
                CreatedAtUtc = ClockNow.AddMinutes(-i),
                IsActive = true,
            });
        }
        await db.SaveChangesAsync();
        var (job, service) = Build(db);
        service.DistributeAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Result<TreasuryPaymentReceiptDto>.Success(
                new TreasuryPaymentReceiptDto(
                    "SQID-1", "x", new DateOnly(2026, 5, 1), "SQID-99",
                    new DateOnly(2026, 4, 1), 0m,
                    nameof(TreasuryPaymentDistributionStatus.Distributed),
                    ClockNow, null, null)));

        await job.Execute(FakeContext());

        await service.Received(3).DistributeAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }
}
