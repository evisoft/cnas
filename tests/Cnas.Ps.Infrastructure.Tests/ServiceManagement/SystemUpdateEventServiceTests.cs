using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.ServiceManagement;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Tests.ServiceManagement;

/// <summary>
/// R2504 / TOR PIR 024 — tests for
/// <see cref="Cnas.Ps.Infrastructure.Services.ServiceManagement.SystemUpdateEventService"/>.
/// </summary>
public sealed class SystemUpdateEventServiceTests
{
    private static async Task<SystemUpdateSchedule> SeedScheduleAsync(
        Cnas.Ps.Infrastructure.Persistence.CnasDbContext db,
        string code = "MONTHLY_PATCH",
        UpdateCadenceKind cadence = UpdateCadenceKind.Monthly,
        int leadTimeDays = 30)
    {
        var schedule = new SystemUpdateSchedule
        {
            ScheduleCode = code,
            Title = "Test schedule",
            Cadence = cadence,
            NoticeLeadTimeDays = leadTimeDays,
            CreatedAtUtc = ServiceManagementTestHelpers.ClockNow,
            CreatedBy = "USR-1",
            IsActive = true,
        };
        db.SystemUpdateSchedules.Add(schedule);
        await db.SaveChangesAsync();
        return schedule;
    }

    [Fact]
    public async Task Create_WithInsufficientLeadTime_Returns_LeadTimeInsufficient()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        await SeedScheduleAsync(db);
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);
        var svc = ServiceManagementTestHelpers.NewEventService(db, audit);

        // Only 5 calendar days, schedule requires 30.
        var planned = ServiceManagementTestHelpers.ClockNow.AddDays(5);
        var result = await svc.CreateAsync(
            new SystemUpdateEventCreateInputDto(
                ScheduleCode: "MONTHLY_PATCH",
                Title: "May patch",
                Description: null,
                PlannedDeploymentUtc: planned,
                MaintenanceWindowSqid: null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.UpdateEventLeadTimeInsufficient);
    }

    [Fact]
    public async Task Create_WithSufficientLeadTime_Succeeds_AndAudits()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        await SeedScheduleAsync(db);
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out var codes);
        var svc = ServiceManagementTestHelpers.NewEventService(db, audit);

        var planned = ServiceManagementTestHelpers.ClockNow.AddDays(35);
        var result = await svc.CreateAsync(
            new SystemUpdateEventCreateInputDto(
                ScheduleCode: "MONTHLY_PATCH",
                Title: "June patch",
                Description: null,
                PlannedDeploymentUtc: planned,
                MaintenanceWindowSqid: null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.EventNumber.Should().StartWith("UPD-2026-");
        codes.Should().Contain(ISystemUpdateEventService.AuditCreated);
    }

    [Fact]
    public async Task Notify_FlipsPlannedToNotified()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        await SeedScheduleAsync(db);
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out var codes);
        var svc = ServiceManagementTestHelpers.NewEventService(db, audit);

        var planned = ServiceManagementTestHelpers.ClockNow.AddDays(35);
        var created = await svc.CreateAsync(
            new SystemUpdateEventCreateInputDto(
                ScheduleCode: "MONTHLY_PATCH",
                Title: "June patch",
                Description: null,
                PlannedDeploymentUtc: planned,
                MaintenanceWindowSqid: null),
            CancellationToken.None);
        created.IsSuccess.Should().BeTrue();

        var notify = await svc.NotifyAsync(created.Value.Id, CancellationToken.None);

        notify.IsSuccess.Should().BeTrue();
        notify.Value.Status.Should().Be(SystemUpdateEventStatus.Notified.ToString());
        notify.Value.NotifiedAt.Should().NotBeNull();
        codes.Should().Contain(ISystemUpdateEventService.AuditNotified);
    }

    [Fact]
    public async Task CompleteDeployment_RequiresDeploying()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        await SeedScheduleAsync(db);
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);
        var svc = ServiceManagementTestHelpers.NewEventService(db, audit);

        var planned = ServiceManagementTestHelpers.ClockNow.AddDays(35);
        var created = await svc.CreateAsync(
            new SystemUpdateEventCreateInputDto(
                ScheduleCode: "MONTHLY_PATCH",
                Title: "June patch",
                Description: null,
                PlannedDeploymentUtc: planned,
                MaintenanceWindowSqid: null),
            CancellationToken.None);
        created.IsSuccess.Should().BeTrue();

        // CompleteDeployment from Planned should fail (need Deploying).
        var fail = await svc.CompleteDeploymentAsync(created.Value.Id, CancellationToken.None);
        fail.IsFailure.Should().BeTrue();
        fail.ErrorCode.Should().Be(ErrorCodes.UpdateEventInvalidTransition);

        // Drive forward through Notify → Start → Complete.
        var notify = await svc.NotifyAsync(created.Value.Id, CancellationToken.None);
        notify.IsSuccess.Should().BeTrue();
        var start = await svc.StartDeploymentAsync(created.Value.Id, CancellationToken.None);
        start.IsSuccess.Should().BeTrue();
        var complete = await svc.CompleteDeploymentAsync(created.Value.Id, CancellationToken.None);
        complete.IsSuccess.Should().BeTrue();
        complete.Value.Status.Should().Be(SystemUpdateEventStatus.Deployed.ToString());
    }
}
