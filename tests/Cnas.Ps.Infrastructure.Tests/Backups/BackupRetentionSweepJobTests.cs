using Cnas.Ps.Application.Backups;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Backups;

/// <summary>
/// R2307 / TOR SEC 060 — tests for <see cref="BackupRetentionSweepJob"/>.
/// </summary>
public sealed class BackupRetentionSweepJobTests
{
    private static IJobExecutionContext NewExecCtx()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private static IServiceScopeFactory NewScopeFactory(IBackupOrchestrator orchestrator)
    {
        var sp = Substitute.For<IServiceProvider>();
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
        var orchestrator = Substitute.For<IBackupOrchestrator>();
        var job = new BackupRetentionSweepJob(
            NewScopeFactory(orchestrator),
            new AlwaysSkipPeakHourGate(),
            NullLogger<BackupRetentionSweepJob>.Instance);

        await job.Execute(NewExecCtx());

        await orchestrator.DidNotReceive().SweepExpiredRunsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_HappyPath_CallsSweep()
    {
        var orchestrator = Substitute.For<IBackupOrchestrator>();
        orchestrator.SweepExpiredRunsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<int>.Success(3)));
        var job = new BackupRetentionSweepJob(
            NewScopeFactory(orchestrator),
            new AllowAllPeakHourGate(),
            NullLogger<BackupRetentionSweepJob>.Instance);

        await job.Execute(NewExecCtx());

        await orchestrator.Received(1).SweepExpiredRunsAsync(Arg.Any<CancellationToken>());
    }
}
