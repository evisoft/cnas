using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Backups;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Backups;
using Cnas.Ps.Infrastructure.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Backups;

/// <summary>
/// R2307 / TOR SEC 060 — tests for <see cref="BackupExecutionJob"/>.
/// </summary>
public sealed class BackupExecutionJobTests
{
    private static IJobExecutionContext NewExecCtx()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private static IServiceScopeFactory NewScopeFactory(
        CnasDbContext db,
        IBackupOrchestrator orchestrator)
    {
        var clock = new BackupTestHelpers.StubClock(BackupTestHelpers.ClockNow);
        var sqids = BackupTestHelpers.NewSqidMock();

        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ICnasDbContext)).Returns(db);
        sp.GetService(typeof(ICnasTimeProvider)).Returns(clock);
        sp.GetService(typeof(ISqidService)).Returns(sqids);
        sp.GetService(typeof(IBackupOrchestrator)).Returns(orchestrator);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    [Fact]
    public async Task Execute_PeakHourGateSkips_NoOps()
    {
        using var db = BackupTestHelpers.CreateContext();
        var orchestrator = Substitute.For<IBackupOrchestrator>();
        var job = new BackupExecutionJob(
            NewScopeFactory(db, orchestrator),
            new AlwaysSkipPeakHourGate(),
            NullLogger<BackupExecutionJob>.Instance);

        await job.Execute(NewExecCtx());

        await orchestrator.DidNotReceive().RunPolicyAsync(
            Arg.Any<string>(), Arg.Any<BackupTriggerKind>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_HappyPath_Triggers_OnePolicyWhoseCronJustFired()
    {
        using var db = BackupTestHelpers.CreateContext();
        // Cron "* * * * * ?" fires every second so the GetTimeAfter(now - 30min) check
        // always returns a fire-time within the window.
        var policy = await BackupTestHelpers.SeedPolicyAsync(db);
        policy.CronSchedule = "* * * * * ?";
        await db.SaveChangesAsync();

        var orchestrator = Substitute.For<IBackupOrchestrator>();
        orchestrator.RunPolicyAsync(Arg.Any<string>(), Arg.Any<BackupTriggerKind>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<Cnas.Ps.Contracts.BackupRunDto>.Success(
                new Cnas.Ps.Contracts.BackupRunDto(
                    Id: "SQID-1",
                    PolicySqid: $"SQID-{policy.Id}",
                    RunNumber: "BKR-2026-000001",
                    Status: BackupRunStatus.Succeeded.ToString(),
                    TriggerKind: BackupTriggerKind.Scheduled.ToString(),
                    StartedAt: BackupTestHelpers.ClockNow,
                    CompletedAt: BackupTestHelpers.ClockNow,
                    DurationMs: 1L,
                    PayloadSizeBytes: 10L,
                    PayloadHashSha256: "h",
                    FailureReason: null,
                    RetentionPurgedAt: null))));

        var job = new BackupExecutionJob(
            NewScopeFactory(db, orchestrator),
            new AllowAllPeakHourGate(),
            NullLogger<BackupExecutionJob>.Instance);

        await job.Execute(NewExecCtx());

        await orchestrator.Received(1).RunPolicyAsync(
            $"SQID-{policy.Id}", BackupTriggerKind.Scheduled, Arg.Any<CancellationToken>());
    }
}
