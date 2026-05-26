using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Migration;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Migration;
using Cnas.Ps.Infrastructure.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Migration;

/// <summary>
/// R2430 / R2431 / R2433 / TOR M4 — tests for <see cref="MigrationDryRunJob"/>.
/// </summary>
public sealed class MigrationDryRunJobTests
{
    private static IJobExecutionContext NewExecCtx()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private static IServiceScopeFactory NewScopeFactory(
        CnasDbContext db,
        IMigrationImporter importer)
    {
        var clock = new MigrationTestHelpers.StubClock(MigrationTestHelpers.ClockNow);
        var sqids = MigrationTestHelpers.NewSqidMock();

        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ICnasDbContext)).Returns(db);
        sp.GetService(typeof(ICnasTimeProvider)).Returns(clock);
        sp.GetService(typeof(ISqidService)).Returns(sqids);
        sp.GetService(typeof(IMigrationImporter)).Returns(importer);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    [Fact]
    public async Task Execute_PeakHourGateSkips_NoOps()
    {
        using var db = MigrationTestHelpers.CreateContext();
        var importer = Substitute.For<IMigrationImporter>();
        var job = new MigrationDryRunJob(
            NewScopeFactory(db, importer),
            new AlwaysSkipPeakHourGate(),
            NullLogger<MigrationDryRunJob>.Instance);

        await job.Execute(NewExecCtx());

        await importer.DidNotReceive().ImportAsync(
            Arg.Any<string>(), Arg.Any<MigrationTriggerKind>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_HappyPath_PicksOneActivePlan()
    {
        using var db = MigrationTestHelpers.CreateContext();
        var audit = MigrationTestHelpers.NewAuditCapturing(out _);
        var src = new InMemoryMigrationSource();
        var plan = await MigrationTestHelpers.SeedPlanAsync(db);
        src.Seed(plan.PlanCode, new[] { MigrationTestHelpers.NewRecord("fp-1") });
        var importer = MigrationTestHelpers.NewImporter(db, src, audit);

        var job = new MigrationDryRunJob(
            NewScopeFactory(db, importer),
            new AllowAllPeakHourGate(),
            NullLogger<MigrationDryRunJob>.Instance);

        await job.Execute(NewExecCtx());

        // After the fire there should be exactly one run for the plan.
        db.MigrationRuns.Should().HaveCount(1);
        var run = db.MigrationRuns.Single();
        run.PlanId.Should().Be(plan.Id);
        run.TriggerKind.Should().Be(MigrationTriggerKind.Scheduled);
    }
}
