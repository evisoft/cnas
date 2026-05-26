using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Quartz.Impl.Matchers;

namespace Cnas.Ps.Infrastructure.Tests.Jobs;

/// <summary>
/// Unit + light-integration tests for <see cref="FailedJobStore"/>. The store wraps
/// EF Core persistence + Quartz <see cref="ISchedulerFactory"/> for replay scheduling.
/// </summary>
/// <remarks>
/// Replay is exercised against a real <see cref="StdSchedulerFactory"/> in standby mode
/// so we can assert the trigger was scheduled without actually running the job —
/// scheduler.Start() is intentionally never called. The scheduler is unique per test
/// (named via a Guid) so concurrent test runs do not collide on the Quartz
/// process-wide registry.
/// </remarks>
public class FailedJobStoreTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 20, 11, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task RecordFailureAsync_PersistsAndReturnsSuccess()
    {
        var harness = await Harness.CreateAsync();
        var entry = new FailedJob
        {
            CreatedAtUtc = ClockNow,
            FailedAtUtc = ClockNow,
            JobName = "mpay-dispatcher",
            JobGroup = "DEFAULT",
            ExceptionType = "System.InvalidOperationException",
            ExceptionMessage = "boom",
            RefireCount = 0,
            IsActive = true,
        };

        var result = await harness.Store.RecordFailureAsync(entry);

        result.IsSuccess.Should().BeTrue();
        var persisted = await harness.Db.FailedJobs.SingleAsync();
        persisted.JobName.Should().Be("mpay-dispatcher");
        persisted.ExceptionMessage.Should().Be("boom");
    }

    [Fact]
    public async Task QueryAsync_FiltersByJobName_ReturnsOnlyMatching()
    {
        var harness = await Harness.CreateAsync();
        await Seed(harness, "mpay-dispatcher", ClockNow);
        await Seed(harness, "mconnect-sync", ClockNow.AddMinutes(-5));
        await Seed(harness, "mpay-dispatcher", ClockNow.AddMinutes(-10));

        var result = await harness.Store.QueryAsync(
            jobName: "mpay-dispatcher", since: null, page: new PageRequest(1, 20));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
        result.Value.Items.Should().OnlyContain(i => i.JobName == "mpay-dispatcher");
        result.Value.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task QueryAsync_OrdersByFailedAtUtcDesc_NewestFirst()
    {
        var harness = await Harness.CreateAsync();
        await Seed(harness, "mpay-dispatcher", ClockNow.AddMinutes(-30));
        await Seed(harness, "mpay-dispatcher", ClockNow);
        await Seed(harness, "mpay-dispatcher", ClockNow.AddMinutes(-15));

        var result = await harness.Store.QueryAsync(
            jobName: null, since: null, page: new PageRequest(1, 20));

        result.IsSuccess.Should().BeTrue();
        var items = result.Value.Items;
        items.Should().HaveCount(3);
        // Newest-first ordering: descending FailedAtUtc.
        items[0].FailedAtUtc.Should().Be(ClockNow);
        items[1].FailedAtUtc.Should().Be(ClockNow.AddMinutes(-15));
        items[2].FailedAtUtc.Should().Be(ClockNow.AddMinutes(-30));
    }

    [Fact]
    public async Task ReplayAsync_NonExistentId_ReturnsNotFound()
    {
        var harness = await Harness.CreateAsync();
        // Sqid decoder returns success for any well-formed string in the stub; the row
        // is what's missing so the store must surface NOT_FOUND.
        var result = await harness.Store.ReplayAsync("nonexistent-sqid");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task ReplayAsync_ValidEntry_SchedulesQuartzJobAndUpdatesState()
    {
        var harness = await Harness.CreateAsync(registerProbeJob: true);
        var entry = new FailedJob
        {
            CreatedAtUtc = ClockNow,
            FailedAtUtc = ClockNow,
            JobName = "probe-job",
            JobGroup = "DEFAULT",
            ExceptionType = "System.InvalidOperationException",
            ExceptionMessage = "first failure",
            RefireCount = 0,
            IsActive = true,
        };
        harness.Db.FailedJobs.Add(entry);
        await harness.Db.SaveChangesAsync();
        // Sqid stub round-trips ids verbatim through "SQID-{id}".
        var sqid = $"SQID-{entry.Id}";

        var result = await harness.Store.ReplayAsync(sqid);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.FailedJobs.SingleAsync(f => f.Id == entry.Id);
        reloaded.ReplayState.Should().Be("scheduled");
        reloaded.LastReplayAtUtc.Should().Be(ClockNow);

        // Trigger was scheduled in Quartz under the dlq-replay group.
        var triggerKeys = await harness.Scheduler.GetTriggerKeys(
            GroupMatcher<TriggerKey>.GroupEquals("dlq-replay"));
        triggerKeys.Should().NotBeEmpty();
    }

    // ─────────────────────── Test plumbing ───────────────────────

    private static async Task Seed(Harness harness, string jobName, DateTime failedAtUtc)
    {
        harness.Db.FailedJobs.Add(new FailedJob
        {
            CreatedAtUtc = failedAtUtc,
            FailedAtUtc = failedAtUtc,
            JobName = jobName,
            JobGroup = "DEFAULT",
            ExceptionType = "System.Exception",
            ExceptionMessage = "test",
            RefireCount = 0,
            IsActive = true,
        });
        await harness.Db.SaveChangesAsync();
    }

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>
    /// Inert Quartz job used to satisfy the "is the JobKey registered with the scheduler"
    /// guard in <see cref="FailedJobStore.ReplayAsync"/>. The job is never actually
    /// executed because the scheduler is left in standby mode.
    /// </summary>
    private sealed class ProbeJob : IJob
    {
        /// <inheritdoc />
        public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required CnasDbContext Db { get; init; }
        public required FailedJobStore Store { get; init; }
        public required ISqidService Sqids { get; init; }
        public required IScheduler Scheduler { get; init; }
        public required ISchedulerFactory SchedulerFactory { get; init; }

        public static async Task<Harness> CreateAsync(bool registerProbeJob = false)
        {
            var db = CreateContext();
            var clock = new StubClock(ClockNow);

            // Build a fresh in-memory Quartz scheduler that we never start. Each test
            // gets its own scheduler name so concurrent runs don't collide.
            var schedulerFactory = Substitute.For<ISchedulerFactory>();
            var schedulerName = $"failedjobstoretests-{Guid.NewGuid():N}";
            var props = new System.Collections.Specialized.NameValueCollection
            {
                ["quartz.scheduler.instanceName"] = schedulerName,
                ["quartz.threadPool.threadCount"] = "1",
                ["quartz.jobStore.type"] = "Quartz.Simpl.RAMJobStore, Quartz",
            };
            var factory = new Quartz.Impl.StdSchedulerFactory(props);
            var scheduler = await factory.GetScheduler();
            // Standby (not Start) — the scheduler is queryable + schedulable but never
            // fires triggers, which is exactly what these tests need.
            if (registerProbeJob)
            {
                var jobDetail = JobBuilder.Create<ProbeJob>()
                    .WithIdentity("probe-job", "DEFAULT")
                    .StoreDurably()
                    .Build();
                await scheduler.AddJob(jobDetail, replace: true);
            }
            schedulerFactory.GetScheduler(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(scheduler));

            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
            sqids.TryDecode(Arg.Any<string>()).Returns(call =>
            {
                var s = call.Arg<string>();
                if (s is null)
                {
                    return Result<long>.Failure(ErrorCodes.InvalidSqid, "null");
                }
                // Accept the "SQID-{n}" round-trip emitted by Encode above; for any
                // other value, return a synthetic miss id so the not-found branch
                // surfaces a NOT_FOUND result (not an InvalidSqid).
                if (s.StartsWith("SQID-", StringComparison.Ordinal)
                    && long.TryParse(s.AsSpan(5), out var v))
                {
                    return Result<long>.Success(v);
                }
                return Result<long>.Success(-1L);
            });

            var store = new FailedJobStore(
                db, schedulerFactory, clock, sqids, NullLogger<FailedJobStore>.Instance);

            return new Harness
            {
                Db = db,
                Store = store,
                Sqids = sqids,
                Scheduler = scheduler,
                SchedulerFactory = schedulerFactory,
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Scheduler.Shutdown();
            await Db.DisposeAsync();
        }
    }
}
