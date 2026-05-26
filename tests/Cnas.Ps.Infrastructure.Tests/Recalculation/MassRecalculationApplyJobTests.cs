using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Recalculation;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Recalculation;
using Cnas.Ps.Infrastructure.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Recalculation;

/// <summary>
/// R1503 / TOR §3.7-D — tests for <see cref="MassRecalculationApplyJob"/>.
/// </summary>
public sealed class MassRecalculationApplyJobTests
{
    private static IJobExecutionContext NewExecCtx()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        ctx.FireInstanceId.Returns("fire-test");
        return ctx;
    }

    private static IServiceScopeFactory NewScopeFactory(
        CnasDbContext db,
        IMassRecalculationService service)
    {
        // NSubstitute disallows configuring one mock inside Returns() of another.
        // Build each substitute eagerly first, THEN wire them into the provider.
        var clock = new RecalculationTestHelpers.StubClock(RecalculationTestHelpers.ClockNow);
        var sqids = RecalculationTestHelpers.NewSqidMock();

        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ICnasDbContext)).Returns(db);
        sp.GetService(typeof(ICnasTimeProvider)).Returns(clock);
        sp.GetService(typeof(IMassRecalculationService)).Returns(service);
        sp.GetService(typeof(ISqidService)).Returns(sqids);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    [Fact]
    public async Task Execute_PeakHourGateSkips_NoOps()
    {
        using var db = RecalculationTestHelpers.CreateContext();
        var svc = Substitute.For<IMassRecalculationService>();
        var scopes = NewScopeFactory(db, svc);
        var job = new MassRecalculationApplyJob(
            scopes,
            new AlwaysSkipPeakHourGate(),
            NullLogger<MassRecalculationApplyJob>.Instance);

        await job.Execute(NewExecCtx());

        await svc.DidNotReceive().StartDryRunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_HappyPath_PicksOldestReadyEventAndStartsDryRun()
    {
        using var db = RecalculationTestHelpers.CreateContext();
        // Seed a Ready event whose effective-from is in the past (relative to ClockNow=2026-05-23).
        var evt = await RecalculationTestHelpers.SeedReadyEventAsync(db);
        evt.EffectiveFrom = new DateOnly(2026, 1, 1);
        await db.SaveChangesAsync();

        var svc = RecalculationTestHelpers.NewMassRecalcService(
            db,
            RecalculationTestHelpers.NewAuditCapturing(out _),
            strategies: Array.Empty<IBenefitRecalculationStrategy>());

        var scopes = NewScopeFactory(db, svc);
        var job = new MassRecalculationApplyJob(
            scopes,
            new AllowAllPeakHourGate(),
            NullLogger<MassRecalculationApplyJob>.Instance);

        await job.Execute(NewExecCtx());

        // The job created a DryRun row against the seeded event.
        var runs = await db.RecalculationRuns.ToListAsync();
        runs.Should().ContainSingle();
        runs[0].LegalChangeEventId.Should().Be(evt.Id);
        runs[0].Mode.Should().Be(RecalculationMode.DryRun);
    }
}
