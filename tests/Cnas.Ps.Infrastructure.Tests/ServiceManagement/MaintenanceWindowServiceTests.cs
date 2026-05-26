using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.ServiceManagement;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Tests.ServiceManagement;

/// <summary>
/// R2502 / TOR PIR 025 — tests for
/// <see cref="Cnas.Ps.Infrastructure.Services.ServiceManagement.MaintenanceWindowService"/>.
/// </summary>
public sealed class MaintenanceWindowServiceTests
{
    private static MaintenanceWindowCreateInputDto NewCreate(
        DateTime startUtc,
        DateTime endUtc,
        MaintenanceWindowKind kind = MaintenanceWindowKind.Ordinary,
        string policyCode = "RM_DEFAULT") => new(
        BusinessHoursPolicyCode: policyCode,
        WindowKind: kind.ToString(),
        Title: "Test window",
        Description: "Routine work — described in some detail.",
        ScheduledStartUtc: startUtc,
        ScheduledEndUtc: endUtc);

    [Fact]
    public async Task Create_HappyPath_PersistsRow_AuditsAndAutoNumbers()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        await ServiceManagementTestHelpers.SeedDefaultPolicyAsync(db);
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out var codes);
        var svc = ServiceManagementTestHelpers.NewMaintenanceService(db, audit);

        // 10 business days from clock-now keeps us safely past Ordinary 5-BD requirement.
        var start = ServiceManagementTestHelpers.ClockNow.AddDays(10);
        var end = start.AddHours(2);
        var result = await svc.CreateAsync(NewCreate(start, end), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.MaintenanceWindows.Should().HaveCount(1);
        codes.Should().Contain(IMaintenanceWindowService.AuditCreated);
        result.Value.WindowNumber.Should().StartWith("MW-2026-");
    }

    [Fact]
    public async Task Create_OrdinaryDurationOver4Hours_Returns_DurationExceeded()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        await ServiceManagementTestHelpers.SeedDefaultPolicyAsync(db);
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);
        var svc = ServiceManagementTestHelpers.NewMaintenanceService(db, audit);

        var start = ServiceManagementTestHelpers.ClockNow.AddDays(10);
        var end = start.AddHours(5);          // 5h > 4h ceiling
        var result = await svc.CreateAsync(
            NewCreate(start, end, MaintenanceWindowKind.Ordinary),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MaintenanceDurationExceeded);
    }

    [Fact]
    public async Task PostNotice_With2BusinessDays_Ordinary_Returns_NoticeLeadTimeInsufficient()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        await ServiceManagementTestHelpers.SeedDefaultPolicyAsync(db);
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);
        var svc = ServiceManagementTestHelpers.NewMaintenanceService(db, audit);

        // 2 calendar days from clock-now (Saturday) → not 5 BD, so notice fails.
        var start = ServiceManagementTestHelpers.ClockNow.AddDays(2);
        var end = start.AddHours(2);
        var created = await svc.CreateAsync(NewCreate(start, end), CancellationToken.None);
        created.IsSuccess.Should().BeTrue();

        var post = await svc.PostNoticeAsync(created.Value.Id, CancellationToken.None);

        post.IsFailure.Should().BeTrue();
        post.ErrorCode.Should().Be(ErrorCodes.MaintenanceNoticeLeadTimeInsufficient);
    }

    [Fact]
    public async Task PostNotice_With10BusinessDays_Ordinary_Succeeds()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        await ServiceManagementTestHelpers.SeedDefaultPolicyAsync(db);
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);
        var svc = ServiceManagementTestHelpers.NewMaintenanceService(db, audit);

        // 10 calendar days from clock-now safely covers ≥ 5 business days.
        var start = ServiceManagementTestHelpers.ClockNow.AddDays(10);
        var end = start.AddHours(2);
        var created = await svc.CreateAsync(NewCreate(start, end), CancellationToken.None);
        created.IsSuccess.Should().BeTrue();

        var post = await svc.PostNoticeAsync(created.Value.Id, CancellationToken.None);

        post.IsSuccess.Should().BeTrue();
        post.Value.Status.Should().Be(MaintenanceWindowStatus.NoticePeriod.ToString());
        post.Value.NoticePostedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Urgent_ImmediateNotice_Succeeds()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        await ServiceManagementTestHelpers.SeedDefaultPolicyAsync(db);
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);
        var svc = ServiceManagementTestHelpers.NewMaintenanceService(db, audit);

        // Urgent windows accept immediate notice (no lead-time requirement).
        var start = ServiceManagementTestHelpers.ClockNow.AddHours(1);
        var end = start.AddHours(1);          // 1h ≤ 2h Urgent ceiling
        var created = await svc.CreateAsync(
            NewCreate(start, end, MaintenanceWindowKind.Urgent),
            CancellationToken.None);
        created.IsSuccess.Should().BeTrue();

        var post = await svc.PostNoticeAsync(created.Value.Id, CancellationToken.None);

        post.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_FromDraft_Returns_Cancelled()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        await ServiceManagementTestHelpers.SeedDefaultPolicyAsync(db);
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out var codes);
        var svc = ServiceManagementTestHelpers.NewMaintenanceService(db, audit);

        var start = ServiceManagementTestHelpers.ClockNow.AddDays(10);
        var end = start.AddHours(2);
        var created = await svc.CreateAsync(NewCreate(start, end), CancellationToken.None);
        created.IsSuccess.Should().BeTrue();

        var cancel = await svc.CancelAsync(
            created.Value.Id,
            new MaintenanceWindowReasonInputDto("operations decided to skip this round"),
            CancellationToken.None);

        cancel.IsSuccess.Should().BeTrue();
        cancel.Value.Status.Should().Be(MaintenanceWindowStatus.Cancelled.ToString());
        codes.Should().Contain(IMaintenanceWindowService.AuditCancelled);
    }
}
