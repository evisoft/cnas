using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ServiceManagement;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.ServiceManagement;

/// <summary>
/// R2504 / TOR PIR 024 — tests for
/// <see cref="SystemUpdateNotificationJob"/>.
/// </summary>
public sealed class SystemUpdateNotificationJobTests
{
    private static IJobExecutionContext NewExecCtx()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private static IServiceScopeFactory NewScopeFactory(
        CnasDbContext db,
        ISystemUpdateEventService eventService)
    {
        var clock = new ServiceManagementTestHelpers.StubClock(ServiceManagementTestHelpers.ClockNow);
        var sqids = ServiceManagementTestHelpers.NewSqidMock();

        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ICnasDbContext)).Returns(db);
        sp.GetService(typeof(ICnasTimeProvider)).Returns(clock);
        sp.GetService(typeof(ISqidService)).Returns(sqids);
        sp.GetService(typeof(ISystemUpdateEventService)).Returns(eventService);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    [Fact]
    public async Task Execute_PeakHourGateSkips_NoOps()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        var svc = Substitute.For<ISystemUpdateEventService>();
        var job = new SystemUpdateNotificationJob(
            NewScopeFactory(db, svc),
            new AlwaysSkipPeakHourGate(),
            NullLogger<SystemUpdateNotificationJob>.Instance);

        await job.Execute(NewExecCtx());

        await svc.DidNotReceive().NotifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_HappyPath_NotifiesEventsApproachingDeadline()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();

        var schedule = new SystemUpdateSchedule
        {
            ScheduleCode = "MONTHLY_PATCH",
            Title = "Monthly patch",
            Cadence = UpdateCadenceKind.Monthly,
            NoticeLeadTimeDays = 30,
            CreatedAtUtc = ServiceManagementTestHelpers.ClockNow,
            CreatedBy = "USR-1",
            IsActive = true,
        };
        db.SystemUpdateSchedules.Add(schedule);
        await db.SaveChangesAsync();

        // PlannedDeploymentUtc is 20 days away; lead-time is 30 → deadline approaching.
        var due = new SystemUpdateEvent
        {
            ScheduleId = schedule.Id,
            EventNumber = "UPD-2026-000001",
            Title = "Due patch",
            PlannedDeploymentUtc = ServiceManagementTestHelpers.ClockNow.AddDays(20),
            Status = SystemUpdateEventStatus.Planned,
            CreatedAtUtc = ServiceManagementTestHelpers.ClockNow,
            CreatedBy = "USR-1",
            IsActive = true,
        };
        // Another event 60 days away — NOT yet due for notice.
        var future = new SystemUpdateEvent
        {
            ScheduleId = schedule.Id,
            EventNumber = "UPD-2026-000002",
            Title = "Future patch",
            PlannedDeploymentUtc = ServiceManagementTestHelpers.ClockNow.AddDays(60),
            Status = SystemUpdateEventStatus.Planned,
            CreatedAtUtc = ServiceManagementTestHelpers.ClockNow,
            CreatedBy = "USR-1",
            IsActive = true,
        };
        db.SystemUpdateEvents.AddRange(due, future);
        await db.SaveChangesAsync();

        var svc = Substitute.For<ISystemUpdateEventService>();
        svc.NotifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<Cnas.Ps.Contracts.SystemUpdateEventDto>.Success(
                new Cnas.Ps.Contracts.SystemUpdateEventDto(
                    Id: "SQID-1",
                    ScheduleSqid: $"SQID-{schedule.Id}",
                    EventNumber: "UPD-2026-000001",
                    Title: "Due patch",
                    Description: null,
                    PlannedDeploymentUtc: due.PlannedDeploymentUtc,
                    Status: SystemUpdateEventStatus.Notified.ToString(),
                    NotifiedAt: ServiceManagementTestHelpers.ClockNow,
                    DeploymentStartedAt: null,
                    DeploymentCompletedAt: null,
                    CancelledAt: null,
                    CancelReason: null,
                    MaintenanceWindowSqid: null))));

        var job = new SystemUpdateNotificationJob(
            NewScopeFactory(db, svc),
            new AllowAllPeakHourGate(),
            NullLogger<SystemUpdateNotificationJob>.Instance);

        await job.Execute(NewExecCtx());

        // Only the due event should have triggered NotifyAsync (exactly once).
        await svc.Received(1).NotifyAsync($"SQID-{due.Id}", Arg.Any<CancellationToken>());
        await svc.DidNotReceive().NotifyAsync($"SQID-{future.Id}", Arg.Any<CancellationToken>());
    }
}
