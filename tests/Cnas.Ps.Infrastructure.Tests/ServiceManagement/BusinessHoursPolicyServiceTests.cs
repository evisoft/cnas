using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.ServiceManagement;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Tests.ServiceManagement;

/// <summary>
/// R2501 / TOR PIR 024 — tests for
/// <see cref="Cnas.Ps.Infrastructure.Services.ServiceManagement.BusinessHoursPolicyService"/>.
/// </summary>
public sealed class BusinessHoursPolicyServiceTests
{
    private static BusinessHoursPolicyCreateInputDto NewCreate(string code = "RM_DEFAULT") => new(
        Code: code,
        DisplayName: "RM business hours",
        Description: null,
        OpenTimeLocal: "08:00",
        CloseTimeLocal: "18:00",
        BusinessDaysMask: 0b0011111,
        TimezoneId: "UTC",
        HolidayDatesJson: null);

    [Fact]
    public async Task Create_HappyPath_PersistsRow_AndAudits()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out var codes);
        var svc = ServiceManagementTestHelpers.NewBusinessHoursService(db, audit);

        var result = await svc.CreateAsync(NewCreate(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.BusinessHoursPolicies.Should().HaveCount(1);
        codes.Should().Contain(IBusinessHoursPolicyService.AuditPolicyCreated);
        result.Value.Code.Should().Be("RM_DEFAULT");
    }

    [Fact]
    public async Task IsBusinessTime_DuringHours_OnWeekday_ReturnsTrue()
    {
        // 2026-05-25 is a Monday. 10:00 UTC sits inside the 08:00–18:00 UTC window
        // of the seed policy (TimezoneId=UTC, Mon–Fri).
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);
        var svc = ServiceManagementTestHelpers.NewBusinessHoursService(db, audit);
        await ServiceManagementTestHelpers.SeedDefaultPolicyAsync(db);

        var mondayMidDay = new DateTime(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc);
        var result = await svc.IsBusinessTimeAsync("RM_DEFAULT", mondayMidDay);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
    }

    [Fact]
    public async Task IsBusinessTime_OnSunday_ReturnsFalse()
    {
        // 2026-05-24 is a Sunday. Even at 10:00 the row is not a business day.
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);
        var svc = ServiceManagementTestHelpers.NewBusinessHoursService(db, audit);
        await ServiceManagementTestHelpers.SeedDefaultPolicyAsync(db);

        var sundayMidDay = new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc);
        var result = await svc.IsBusinessTimeAsync("RM_DEFAULT", sundayMidDay);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeFalse();
    }

    [Fact]
    public async Task AddBusinessDays_SkipsWeekend()
    {
        // Start: Friday 2026-05-22 10:00 UTC, + 1 business day = Monday 2026-05-25.
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);
        var svc = ServiceManagementTestHelpers.NewBusinessHoursService(db, audit);
        await ServiceManagementTestHelpers.SeedDefaultPolicyAsync(db);

        var friday = new DateTime(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc);
        var result = await svc.AddBusinessDaysAsync("RM_DEFAULT", friday, 1);

        result.IsSuccess.Should().BeTrue();
        // Monday 2026-05-25 10:00 UTC
        result.Value.Year.Should().Be(2026);
        result.Value.Month.Should().Be(5);
        result.Value.Day.Should().Be(25);
        result.Value.DayOfWeek.Should().Be(DayOfWeek.Monday);
    }

    [Fact]
    public async Task AddBusinessDays_SkipsHoliday()
    {
        // 2026-05-25 is Monday (RM Independence Day proxy in this test);
        // + 1 BD from Friday should land on Tuesday 2026-05-26.
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);
        var svc = ServiceManagementTestHelpers.NewBusinessHoursService(db, audit);
        await ServiceManagementTestHelpers.SeedDefaultPolicyAsync(
            db,
            holidayDatesJson: "[\"2026-05-25\"]");

        var friday = new DateTime(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc);
        var result = await svc.AddBusinessDaysAsync("RM_DEFAULT", friday, 1);

        result.IsSuccess.Should().BeTrue();
        result.Value.DayOfWeek.Should().Be(DayOfWeek.Tuesday);
        result.Value.Day.Should().Be(26);
    }

    [Fact]
    public async Task Create_DuplicateCode_Returns_Conflict()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);
        var svc = ServiceManagementTestHelpers.NewBusinessHoursService(db, audit);

        var first = await svc.CreateAsync(NewCreate(), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        var second = await svc.CreateAsync(NewCreate(), CancellationToken.None);
        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.BusinessHoursPolicyDuplicateCode);
    }
}
