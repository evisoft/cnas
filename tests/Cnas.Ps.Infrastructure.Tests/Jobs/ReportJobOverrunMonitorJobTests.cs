using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Notifications;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Jobs;

/// <summary>
/// R0174 / TOR CF 22.03 — tests for the PerformanceAlert canonical trigger.
/// Pins the overrun-sweep behaviour of <see cref="ReportJobOverrunMonitorJob"/>:
/// rows running past the configured threshold fire exactly one
/// <see cref="NotificationTriggerKind.PerformanceAlert"/> per row.
/// </summary>
public sealed class ReportJobOverrunMonitorJobTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ExecuteAsync_OverdueRunningRow_FiresPerformanceAlertExactlyOnce()
    {
        var harness = await Harness.CreateAsync(thresholdMinutes: 5);
        await harness.SeedReportJobAsync(
            requestedByUserId: 11L,
            status: ReportJobStatus.Running,
            startedAtUtc: ClockNow.AddMinutes(-10));

        var dispatched = await harness.Job.ExecuteAsync();

        dispatched.Should().Be(1);
        await harness.Triggers.Received(1).DispatchAsync(
            NotificationTriggerKind.PerformanceAlert,
            Arg.Is<NotificationTriggerPayload>(p =>
                p.RecipientUserId == 11L
                && p.RelatedEntityType == NotificationRelatedEntityTypes.ReportRun),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RecentRow_DoesNotFire()
    {
        // Started 2 minutes ago, threshold is 5 → not stale.
        var harness = await Harness.CreateAsync(thresholdMinutes: 5);
        await harness.SeedReportJobAsync(
            requestedByUserId: 21L,
            status: ReportJobStatus.Running,
            startedAtUtc: ClockNow.AddMinutes(-2));

        var dispatched = await harness.Job.ExecuteAsync();

        dispatched.Should().Be(0);
        await harness.Triggers.DidNotReceive().DispatchAsync(
            Arg.Any<NotificationTriggerKind>(),
            Arg.Any<NotificationTriggerPayload>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyFlaggedRow_DoesNotFireAgain()
    {
        // The sentinel prefix on FailureReason means the row was already alerted.
        var harness = await Harness.CreateAsync(thresholdMinutes: 5);
        var row = await harness.SeedReportJobAsync(
            requestedByUserId: 31L,
            status: ReportJobStatus.Running,
            startedAtUtc: ClockNow.AddMinutes(-30));
        row.FailureReason = ReportJobOverrunMonitorJob.OverrunSentinelPrefix + " threshold=5m";
        await harness.Db.SaveChangesAsync();

        var dispatched = await harness.Job.ExecuteAsync();

        dispatched.Should().Be(0);
        await harness.Triggers.DidNotReceive().DispatchAsync(
            Arg.Any<NotificationTriggerKind>(),
            Arg.Any<NotificationTriggerPayload>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_DisabledThreshold_DoesNothing()
    {
        var harness = await Harness.CreateAsync(thresholdMinutes: 0);
        await harness.SeedReportJobAsync(
            requestedByUserId: 41L,
            status: ReportJobStatus.Running,
            startedAtUtc: ClockNow.AddMinutes(-30));

        var dispatched = await harness.Job.ExecuteAsync();

        dispatched.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_StampsSentinelBeforeDispatching()
    {
        var harness = await Harness.CreateAsync(thresholdMinutes: 5);
        var row = await harness.SeedReportJobAsync(
            requestedByUserId: 51L,
            status: ReportJobStatus.Running,
            startedAtUtc: ClockNow.AddMinutes(-30));

        await harness.Job.ExecuteAsync();

        var reloaded = await harness.Db.ReportJobs.SingleAsync(j => j.Id == row.Id);
        reloaded.FailureReason.Should()
            .StartWith(ReportJobOverrunMonitorJob.OverrunSentinelPrefix);
    }

    // ─── harness ───

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required INotificationTriggerDispatcher Triggers { get; init; }
        public required ReportJobOverrunMonitorJob Job { get; init; }

        public static async Task<Harness> CreateAsync(int thresholdMinutes)
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rj-overrun-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var clock = new StubClock(ClockNow);
            var triggers = Substitute.For<INotificationTriggerDispatcher>();
            triggers.DispatchAsync(
                Arg.Any<NotificationTriggerKind>(),
                Arg.Any<NotificationTriggerPayload>(),
                Arg.Any<CancellationToken>())
                .Returns(Result.Success());
            var options = Options.Create(new ReportJobOverrunOptions
            {
                OverrunThresholdMinutes = thresholdMinutes,
            });
            var job = new ReportJobOverrunMonitorJob(
                db, clock, triggers,
                NullLogger<ReportJobOverrunMonitorJob>.Instance,
                options);
            await Task.CompletedTask;
            return new Harness { Db = db, Triggers = triggers, Job = job };
        }

        public async Task<ReportJob> SeedReportJobAsync(
            long requestedByUserId,
            ReportJobStatus status,
            DateTime startedAtUtc)
        {
            var row = new ReportJob
            {
                ReportTemplateId = 7L,
                RequestedByUserId = requestedByUserId,
                Format = 0,
                Status = status,
                QueuedAtUtc = startedAtUtc.AddMinutes(-1),
                StartedAtUtc = startedAtUtc,
                CreatedAtUtc = startedAtUtc,
                IsActive = true,
            };
            Db.ReportJobs.Add(row);
            await Db.SaveChangesAsync();
            return row;
        }
    }
}
