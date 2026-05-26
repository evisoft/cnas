using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.ServiceManagement;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers.ServiceManagement;

/// <summary>
/// R2502 / TOR PIR 025 — tests for <see cref="MaintenanceWindowsController"/>.
/// </summary>
public sealed class MaintenanceWindowsControllerTests
{
    private static MaintenanceWindowDto NewDto(string id = "SQID-1") => new(
        Id: id,
        WindowNumber: "MW-2026-000001",
        BusinessHoursPolicySqid: "SQID-99",
        WindowKind: MaintenanceWindowKind.Ordinary.ToString(),
        Title: "Test",
        Description: "Detailed description.",
        ScheduledStartUtc: new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc),
        ScheduledEndUtc: new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
        Status: MaintenanceWindowStatus.Draft.ToString(),
        NoticePostedAt: null,
        ApprovedAt: null,
        StartedAt: null,
        CompletedAt: null,
        CancelledAt: null,
        CancelReason: null);

    [Fact]
    public async Task Create_HappyPath_Returns200()
    {
        var svc = Substitute.For<IMaintenanceWindowService>();
        svc.CreateAsync(Arg.Any<MaintenanceWindowCreateInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<MaintenanceWindowDto>.Success(NewDto())));
        var controller = new MaintenanceWindowsController(svc);

        var input = new MaintenanceWindowCreateInputDto(
            BusinessHoursPolicyCode: "RM_DEFAULT",
            WindowKind: "Ordinary",
            Title: "Test",
            Description: "Detailed description.",
            ScheduledStartUtc: new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc),
            ScheduledEndUtc: new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));

        var result = await controller.CreateAsync(input, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<MaintenanceWindowDto>();
    }

    [Fact]
    public async Task PostNotice_NoticeLeadTimeInsufficient_Returns409()
    {
        var svc = Substitute.For<IMaintenanceWindowService>();
        svc.PostNoticeAsync("SQID-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<MaintenanceWindowDto>.Failure(
                ErrorCodes.MaintenanceNoticeLeadTimeInsufficient,
                "Ordinary windows require 5 business days.")));
        var controller = new MaintenanceWindowsController(svc);

        var result = await controller.PostNoticeAsync("SQID-1", CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task GetById_HappyPath_Returns200()
    {
        var dto = NewDto();
        var svc = Substitute.For<IMaintenanceWindowService>();
        svc.GetByIdAsync("SQID-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<MaintenanceWindowDto>.Success(dto)));
        var controller = new MaintenanceWindowsController(svc);

        var result = await controller.GetByIdAsync("SQID-1", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }
}
