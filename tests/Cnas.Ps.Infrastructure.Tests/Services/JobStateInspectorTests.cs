using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0204 / TOR CF 20.07-08 — tests for <see cref="JobStateInspector"/>. The inspector
/// reads Quartz scheduler state and projects it into <see cref="Cnas.Ps.Contracts.JobStateDto"/>
/// rows for the admin Jobs dashboard.
/// </summary>
/// <remarks>
/// Each test spins up a fresh standby Quartz scheduler (named with a unique Guid so
/// parallel test runs do not collide on the Quartz process-wide registry) and exercises
/// the inspector against it. The scheduler is never started, so jobs do not actually
/// fire — but their schedule metadata is fully queryable, which is exactly what the
/// dashboard needs.
/// </remarks>
public sealed class JobStateInspectorTests
{
    /// <summary>Builds a fresh standby Quartz scheduler unique to this test invocation.</summary>
    private static async Task<IScheduler> NewSchedulerAsync()
    {
        var props = new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"jobstateinspectortests-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
            ["quartz.jobStore.type"] = "Quartz.Simpl.RAMJobStore, Quartz",
        };
        var factory = new Quartz.Impl.StdSchedulerFactory(props);
        var scheduler = await factory.GetScheduler();
        return scheduler;
    }

    /// <summary>Builds the SUT around the supplied scheduler.</summary>
    private static JobStateInspector NewInspector(IScheduler scheduler)
    {
        var schedulerFactory = Substitute.For<ISchedulerFactory>();
        schedulerFactory.GetScheduler(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(scheduler));
        return new JobStateInspector(schedulerFactory, NullLogger<JobStateInspector>.Instance);
    }

    [Fact]
    public async Task ListAsync_EmptyScheduler_ReturnsEmptyList()
    {
        var scheduler = await NewSchedulerAsync();
        var inspector = NewInspector(scheduler);

        var result = await inspector.ListAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_SingleJob_ReturnsOneRowWithJobNameAndTriggerState()
    {
        var scheduler = await NewSchedulerAsync();
        var inspector = NewInspector(scheduler);
        await ScheduleProbeJobAsync(scheduler, "probe-1", "0 0 12 * * ?");

        var result = await inspector.ListAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var rows = result.Value!;
        rows.Should().HaveCount(1);
        rows[0].JobName.Should().Be("probe-1");
        rows[0].JobGroup.Should().Be("DEFAULT");
        rows[0].State.Should().Be("Normal");
        // A cron trigger has no last-fire until the scheduler runs it.
        rows[0].LastFireUtc.Should().BeNull();
        rows[0].NextFireUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ListAsync_PausedJob_ReturnsPausedState()
    {
        var scheduler = await NewSchedulerAsync();
        var inspector = NewInspector(scheduler);
        var triggerKey = await ScheduleProbeJobAsync(scheduler, "probe-paused", "0 0 12 * * ?");
        await scheduler.PauseTrigger(triggerKey);

        var result = await inspector.ListAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var rows = result.Value!;
        rows.Should().ContainSingle()
            .Which.State.Should().Be("Paused");
    }

    [Fact]
    public async Task ListAsync_MultipleJobs_ReturnsAlphabeticallyOrderedByJobName()
    {
        var scheduler = await NewSchedulerAsync();
        var inspector = NewInspector(scheduler);
        await ScheduleProbeJobAsync(scheduler, "zeta", "0 0 12 * * ?");
        await ScheduleProbeJobAsync(scheduler, "alpha", "0 0 12 * * ?");
        await ScheduleProbeJobAsync(scheduler, "mu", "0 0 12 * * ?");

        var result = await inspector.ListAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var rows = result.Value!;
        rows.Should().HaveCount(3);
        rows.Select(r => r.JobName).Should().BeInAscendingOrder();
        rows[0].JobName.Should().Be("alpha");
        rows[1].JobName.Should().Be("mu");
        rows[2].JobName.Should().Be("zeta");
    }

    private static async Task<TriggerKey> ScheduleProbeJobAsync(
        IScheduler scheduler, string name, string cron)
    {
        var jobKey = new JobKey(name);
        var triggerKey = new TriggerKey($"{name}-trigger");

        var jobDetail = JobBuilder.Create<ProbeJob>()
            .WithIdentity(jobKey)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .ForJob(jobKey)
            .WithCronSchedule(cron)
            .Build();

        await scheduler.ScheduleJob(jobDetail, trigger);
        return triggerKey;
    }

    /// <summary>Inert Quartz job — needed because the scheduler refuses jobs with no class.</summary>
    private sealed class ProbeJob : IJob
    {
        /// <inheritdoc />
        public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
    }
}
