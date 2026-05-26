using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Treasury.Feed;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Treasury.Feed;
using Cnas.Ps.Infrastructure.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Treasury.Feed;

/// <summary>
/// R1810 / TOR BP 1.2-I — tests for <see cref="TreasuryFeedImportJob"/>.
/// </summary>
public sealed class TreasuryFeedImportJobTests
{
    private static IJobExecutionContext NewExecCtx()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private static IServiceScopeFactory NewScopeFactory(
        CnasDbContext db,
        ITreasuryFeedImporter importer)
    {
        var clock = new TreasuryFeedTestHelpers.StubClock(TreasuryFeedTestHelpers.ClockNow);

        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ICnasDbContext)).Returns(db);
        sp.GetService(typeof(ICnasTimeProvider)).Returns(clock);
        sp.GetService(typeof(ITreasuryFeedImporter)).Returns(importer);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    /// <summary>Peak-hour gate Skip turns the fire into a no-op.</summary>
    [Fact]
    public async Task Execute_PeakHourGateSkips_NoOps()
    {
        using var db = TreasuryFeedTestHelpers.CreateContext();
        var importer = Substitute.For<ITreasuryFeedImporter>();
        var job = new TreasuryFeedImportJob(
            NewScopeFactory(db, importer),
            new AlwaysSkipPeakHourGate(),
            NullLogger<TreasuryFeedImportJob>.Instance);

        await job.Execute(NewExecCtx());

        await importer.DidNotReceive().ImportAsync(
            Arg.Any<DateOnly>(),
            Arg.Any<TreasuryFeedTriggerKind>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Happy path: gate Allow + no prior Completed row → the importer is
    /// invoked for yesterday (UTC) with TriggerKind=Scheduled.
    /// </summary>
    [Fact]
    public async Task Execute_HappyPath_ImportsForYesterday()
    {
        using var db = TreasuryFeedTestHelpers.CreateContext();
        await TreasuryFeedTestHelpers.SeedContributorAsync(db, "1000000000003");
        var src = new InMemoryTreasuryFeedSource();
        var yesterday = new DateOnly(2026, 5, 22); // clock day is 2026-05-23
        src.Seed(yesterday, TreasuryFeedTestHelpers.BuildCsv(
            ("TR-001", "2026-05-22", "1000000000003", "Test Payer", "100.00", "MD12", "ref")));

        var audit = TreasuryFeedTestHelpers.NewAuditCapturing(out _);
        var importer = TreasuryFeedTestHelpers.NewImporter(db, src, audit);
        var job = new TreasuryFeedImportJob(
            NewScopeFactory(db, importer),
            new AllowAllPeakHourGate(),
            NullLogger<TreasuryFeedImportJob>.Instance);

        await job.Execute(NewExecCtx());

        var imports = await db.TreasuryFeedImports.ToListAsync();
        imports.Should().ContainSingle();
        imports[0].FeedDate.Should().Be(yesterday);
        imports[0].Status.Should().Be(TreasuryFeedImportStatus.Completed);
        imports[0].TriggerKind.Should().Be(TreasuryFeedTriggerKind.Scheduled);
    }
}
