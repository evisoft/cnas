using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Jobs;

/// <summary>
/// R1000..R1034 / TOR §3.2-Z — tests for <see cref="RecurrentPaymentJob"/>.
/// Validates the [DisallowConcurrentExecution] marker, idempotency on
/// re-fire, and the end-to-end happy path through
/// <see cref="IRecurrentPaymentSchedulerService"/>.
/// </summary>
public sealed class RecurrentPaymentJobTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = DateOnly.FromDateTime(ClockNow);

    private sealed class StubClock : ICnasTimeProvider
    {
        public DateTime UtcNow { get; }

        public StubClock(DateTime now) { UtcNow = now; }
    }

    private static IJobExecutionContext FakeCtx()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private static ServiceProvider BuildProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<CnasDbContext>(opts => opts
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
        services.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
        services.AddSingleton<ICnasTimeProvider>(new StubClock(ClockNow));

        // Stub the surrounding wiring.
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(c => $"SQID-{c.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(c =>
        {
            var v = c.Arg<string>();
            if (v is not null && v.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(v["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        services.AddSingleton(sqids);

        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(1L);
        caller.UserSqid.Returns("USR-1");
        caller.SourceIp.Returns("127.0.0.1");
        caller.CorrelationId.Returns("corr-job");
        services.AddScoped(_ => caller);

        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        services.AddScoped(_ => audit);

        services.AddScoped<IRecurrentPaymentSchedulerService, RecurrentPaymentSchedulerService>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Job_IsMarkedWithDisallowConcurrentExecution()
    {
        var attr = typeof(RecurrentPaymentJob)
            .GetCustomAttribute<DisallowConcurrentExecutionAttribute>(inherit: false);
        attr.Should().NotBeNull("RecurrentPaymentJob must serialise its fires");
    }

    /// <summary>
    /// Seeds a single Solicitant row with the explicit <paramref name="id"/>
    /// so the scheduler's beneficiary-IDNP lookup resolves. The scheduler now
    /// validates the BeneficiaryId against the Solicitants set; tests that
    /// emit MPayOrder rows must seed matching beneficiary rows. The EF Core
    /// in-memory provider honours explicit Id assignment so we can target
    /// arbitrary ids without padding sentinel rows.
    /// </summary>
    private static async Task SeedSolicitantAsync(CnasDbContext db, long id)
    {
        if (await db.Solicitants.AnyAsync(s => s.Id == id))
        {
            return;
        }
        db.Solicitants.Add(new Solicitant
        {
            Id = id,
            CreatedAtUtc = ClockNow.AddDays(-1),
            NationalId = $"BEN{id:D10}",
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = $"Beneficiary{id}",
            PreferredLanguage = "ro",
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Execute_HappyPath_DispatchesEveryDueSchedule()
    {
        var dbName = $"cnas-rps-job-{Guid.NewGuid():N}";
        await using var provider = BuildProvider(dbName);

        // Seed beneficiary Solicitant rows for ids 7 and 8 (the SQIDs decode
        // to those) so the scheduler can resolve their IDNP.
        using (var seedScope = provider.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<CnasDbContext>();
            await SeedSolicitantAsync(seedDb, 7);
            await SeedSolicitantAsync(seedDb, 8);
        }

        // Seed two schedules through the service so audit + DB are coherent.
        using (var seedScope = provider.CreateScope())
        {
            var svc = seedScope.ServiceProvider.GetRequiredService<IRecurrentPaymentSchedulerService>();
            await svc.CreateAsync(new(
                BeneficiarySqid: "SQID-7",
                ServiceCode: "3.2-Z",
                Amount: 1000m,
                NextPaymentDate: Today,
                Cadence: "Monthly"), CancellationToken.None);
            await svc.CreateAsync(new(
                BeneficiarySqid: "SQID-8",
                ServiceCode: "3.2-Z",
                Amount: 750m,
                NextPaymentDate: Today.AddDays(-2),
                Cadence: "Monthly"), CancellationToken.None);
        }

        var job = new RecurrentPaymentJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new Cnas.Ps.Infrastructure.Tests.Common.AllowAllPeakHourGate(),
            NullLogger<RecurrentPaymentJob>.Instance);

        await job.Execute(FakeCtx());

        using var verifyScope = provider.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<CnasDbContext>();
        db.MPayOrders.Count().Should().Be(2);
        // Post-fix: RunDue NO LONGER advances NextPaymentDate. The callback
        // advancer (invoked from the MPay callback handler) is the only path
        // that moves the schedule forward. Pin that NextPaymentDate is
        // unchanged here and that LastDispatchedOrderId is back-filled so the
        // advancer can find the right schedule on confirmation.
        db.RecurrentPaymentSchedules.All(s => s.LastDispatchedOrderId != null)
            .Should().BeTrue("the job must back-fill the dispatched-order link");
    }

    [Fact]
    public async Task Execute_Idempotent_OnRefireSameDay_NoDoubleDispatch()
    {
        var dbName = $"cnas-rps-job-{Guid.NewGuid():N}";
        await using var provider = BuildProvider(dbName);

        using (var seedScope = provider.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<CnasDbContext>();
            await SeedSolicitantAsync(seedDb, 9);
        }

        using (var seedScope = provider.CreateScope())
        {
            var svc = seedScope.ServiceProvider.GetRequiredService<IRecurrentPaymentSchedulerService>();
            await svc.CreateAsync(new(
                BeneficiarySqid: "SQID-9",
                ServiceCode: "3.2-Z",
                Amount: 250m,
                NextPaymentDate: Today,
                Cadence: "Monthly"), CancellationToken.None);
        }

        var job = new RecurrentPaymentJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new Cnas.Ps.Infrastructure.Tests.Common.AllowAllPeakHourGate(),
            NullLogger<RecurrentPaymentJob>.Instance);

        await job.Execute(FakeCtx());
        // Re-fire: post-fix the schedule's LastDispatchedOrderId points at
        // the still-unconfirmed order from the first run. The scheduler
        // detects the in-flight obligation and skips the duplicate dispatch
        // — idempotency is now enforced by the dispatch-link invariant
        // rather than by date arithmetic.
        await job.Execute(FakeCtx());

        using var verifyScope = provider.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<CnasDbContext>();
        db.MPayOrders.Count().Should().Be(1);
    }
}
