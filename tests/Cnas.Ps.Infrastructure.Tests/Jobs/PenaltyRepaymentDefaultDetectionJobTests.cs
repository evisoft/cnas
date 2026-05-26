using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Financials;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Financials;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Jobs;

/// <summary>
/// R0817 / TOR BP 1.2-H — tests for
/// <see cref="PenaltyRepaymentDefaultDetectionJob"/>. Verifies the job
/// iterates Active plans with at least one installment overdue more than
/// <see cref="PenaltyRepaymentService.DefaultDetectionWindowDays"/> days.
/// </summary>
public sealed class PenaltyRepaymentDefaultDetectionJobTests
{
    /// <summary>Fixed UTC clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Today's date in UTC under the test clock.</summary>
    private static readonly DateOnly Today = DateOnly.FromDateTime(ClockNow);

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
            .UseInMemoryDatabase($"cnas-penalty-job-{Guid.NewGuid():N}")
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

    /// <summary>Builds the job + scope factory wiring.</summary>
    private static (PenaltyRepaymentDefaultDetectionJob Job, IPenaltyRepaymentService Service) Build(
        ICnasDbContext db)
    {
        var service = Substitute.For<IPenaltyRepaymentService>();
        var clock = new StubClock(ClockNow);
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ICnasDbContext)).Returns(db);
        sp.GetService(typeof(ICnasTimeProvider)).Returns(clock);
        sp.GetService(typeof(IPenaltyRepaymentService)).Returns(service);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var job = new PenaltyRepaymentDefaultDetectionJob(
            scopeFactory,
            new Cnas.Ps.Infrastructure.Tests.Common.AllowAllPeakHourGate(),
            NullLogger<PenaltyRepaymentDefaultDetectionJob>.Instance);
        return (job, service);
    }

    /// <summary>Seeds a plan + N installments. The first installment is overdue by <paramref name="overdueDays"/>.</summary>
    private static async Task<long> SeedPlanWithOverdueInstallmentAsync(
        CnasDbContext db,
        int overdueDays)
    {
        var plan = new PenaltyRepaymentPlan
        {
            LatePaymentPenaltyId = 1L,
            InstallmentCount = 3,
            InstallmentAmount = 33.33m,
            FirstInstallmentDueDate = Today.AddDays(-overdueDays),
            Status = PenaltyRepaymentPlanStatus.Active,
            PaidInstallmentCount = 0,
            RemainingAmount = 100m,
            CreatedUtc = ClockNow.AddDays(-60),
            CreatedAtUtc = ClockNow.AddDays(-60),
            IsActive = true,
        };
        db.PenaltyRepaymentPlans.Add(plan);
        await db.SaveChangesAsync();
        db.PenaltyRepaymentInstallments.Add(new PenaltyRepaymentInstallment
        {
            PenaltyRepaymentPlanId = plan.Id,
            InstallmentNumber = 1,
            DueDate = Today.AddDays(-overdueDays),
            Amount = 33.33m,
            IsPaid = false,
            CreatedAtUtc = ClockNow.AddDays(-60),
            IsActive = true,
        });
        await db.SaveChangesAsync();
        return plan.Id;
    }

    /// <summary>R0817 — Active plans with overdue installments are handed to MarkDefaultedAsync.</summary>
    [Fact]
    public async Task Execute_IteratesActivePlansAndMarksOverdueAsDefaulted()
    {
        using var db = CreateContext();
        var overduePlanId = await SeedPlanWithOverdueInstallmentAsync(db, overdueDays: 45);
        var (job, service) = Build(db);
        service.MarkDefaultedAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        await job.Execute(FakeContext());

        await service.Received(1).MarkDefaultedAsync(overduePlanId, Arg.Any<CancellationToken>());
    }

    /// <summary>R0817 — plans without an installment overdue beyond the threshold are skipped.</summary>
    [Fact]
    public async Task Execute_FreshOverdueWithin30Days_SkipsPlan()
    {
        using var db = CreateContext();
        await SeedPlanWithOverdueInstallmentAsync(db, overdueDays: 5);
        var (job, service) = Build(db);

        await job.Execute(FakeContext());

        await service.DidNotReceive().MarkDefaultedAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }
}
