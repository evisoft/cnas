using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.ManagementPeriods;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0820 / BP 1.2-K — controller-level tests for
/// <see cref="ManagementPeriodController"/>. Verifies success / failure
/// routing for the close endpoint.
/// </summary>
public sealed class ManagementPeriodControllerTests
{
    /// <summary>Canonical month anchor used in the suite.</summary>
    private static readonly DateOnly Month = new(2026, 4, 1);

    /// <summary>Stock DTO returned by the service mock on success.</summary>
    private static ManagementPeriodCloseDto SampleDto() => new(
        Id: "MPC-1",
        Month: Month,
        ClosedAtUtc: new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
        ClosedByUserSqid: "USR-7",
        Notes: "End of April",
        TotalDeclaredAcrossPayers: 1000m,
        TotalPaidAcrossPayers: 950m,
        PayerCount: 3,
        DeclarationCount: 5,
        IsReopened: false,
        ReopenedAtUtc: null,
        ReopenedByUserSqid: null,
        ReopenReason: null);

    /// <summary>R0820 — POST /api/management-period/{month}/close returns 200 with the DTO on success.</summary>
    [Fact]
    public async Task Close_ServiceReturnsSuccess_Returns200()
    {
        var svc = Substitute.For<IManagementPeriodService>();
        svc.CloseAsync(Month, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<ManagementPeriodCloseDto>.Success(SampleDto()));
        var controller = new ManagementPeriodController(svc);

        var result = await controller.CloseAsync(
            Month,
            new ManagementPeriodCloseInputDto(Month, "End of April"),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ManagementPeriodCloseDto>().Subject;
        dto.Id.Should().Be("MPC-1");
        dto.Month.Should().Be(Month);
    }

    /// <summary>R0820 — POST close on an already-closed month returns 409.</summary>
    [Fact]
    public async Task Close_ServiceReturnsConflict_Returns409()
    {
        var svc = Substitute.For<IManagementPeriodService>();
        svc.CloseAsync(Month, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<ManagementPeriodCloseDto>.Failure(ErrorCodes.Conflict, "MONTH_ALREADY_CLOSED"));
        var controller = new ManagementPeriodController(svc);

        var result = await controller.CloseAsync(
            Month,
            null,
            CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(409);
    }
}
