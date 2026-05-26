using System;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R2501-R2504 — unit tests for the service-management input validators.
/// </summary>
public sealed class ServiceManagementInputValidatorTests
{
    [Fact]
    public void BusinessHoursPolicyCreate_AllValid_Passes()
    {
        var v = new BusinessHoursPolicyCreateInputValidator();
        var dto = new BusinessHoursPolicyCreateInputDto(
            Code: "RM_DEFAULT",
            DisplayName: "RM business hours",
            Description: null,
            OpenTimeLocal: "08:00",
            CloseTimeLocal: "18:00",
            BusinessDaysMask: 0b0011111,
            TimezoneId: "Europe/Chisinau",
            HolidayDatesJson: null);

        var result = v.Validate(dto);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void BusinessHoursPolicyCreate_LowerCaseCode_Fails()
    {
        var v = new BusinessHoursPolicyCreateInputValidator();
        var dto = new BusinessHoursPolicyCreateInputDto(
            Code: "rm_default",
            DisplayName: "RM business hours",
            Description: null,
            OpenTimeLocal: "08:00",
            CloseTimeLocal: "18:00",
            BusinessDaysMask: 31,
            TimezoneId: "Europe/Chisinau",
            HolidayDatesJson: null);

        var result = v.Validate(dto);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void MaintenanceWindowCreate_StartAfterEnd_Fails()
    {
        var v = new MaintenanceWindowCreateInputValidator();
        var start = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var dto = new MaintenanceWindowCreateInputDto(
            BusinessHoursPolicyCode: "RM_DEFAULT",
            WindowKind: "Ordinary",
            Title: "Title",
            Description: "Detailed description",
            ScheduledStartUtc: start,
            ScheduledEndUtc: start.AddHours(-1));  // end < start

        var result = v.Validate(dto);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void MaintenanceWindowCreate_UnknownKind_Fails()
    {
        var v = new MaintenanceWindowCreateInputValidator();
        var start = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var dto = new MaintenanceWindowCreateInputDto(
            BusinessHoursPolicyCode: "RM_DEFAULT",
            WindowKind: "Unknown",
            Title: "Title",
            Description: "Detailed description",
            ScheduledStartUtc: start,
            ScheduledEndUtc: start.AddHours(1));

        var result = v.Validate(dto);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void SystemUpdateScheduleCreate_NegativeLeadTime_Fails()
    {
        var v = new SystemUpdateScheduleCreateInputValidator();
        var dto = new SystemUpdateScheduleCreateInputDto(
            ScheduleCode: "MONTHLY_PATCH",
            Title: "Monthly patch",
            Cadence: "Monthly",
            NoticeLeadTimeDays: -1,
            Description: null);

        var result = v.Validate(dto);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void SystemUpdateEventCreate_HappyPath_Passes()
    {
        var v = new SystemUpdateEventCreateInputValidator();
        var dto = new SystemUpdateEventCreateInputDto(
            ScheduleCode: "MONTHLY_PATCH",
            Title: "June patch",
            Description: null,
            PlannedDeploymentUtc: new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc),
            MaintenanceWindowSqid: null);

        var result = v.Validate(dto);
        result.IsValid.Should().BeTrue();
    }
}
