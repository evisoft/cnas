using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.WorkflowTasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0127 / CF 16.11 — controller-side tests for <see cref="UserAbsencesController"/>.
/// </summary>
public sealed class UserAbsencesControllerTests
{
    private static readonly DateTime Now = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task PlanAsync_HappyPath_Returns201WithLocation()
    {
        var svc = Substitute.For<IUserAbsenceService>();
        var sqids = Substitute.For<ISqidService>();
        var dto = new UserAbsenceOutputDto(
            Id: "ABS1",
            UserSqid: "USER1",
            DelegateSqid: "USER2",
            StartDateUtc: Now.AddDays(1),
            EndDateUtc: Now.AddDays(5),
            Status: "Planned",
            ActivatedAtUtc: null,
            CompletedAtUtc: null,
            RoutedTaskCount: 0,
            Reason: "Concediu");
        var body = new UserAbsenceCreateDto(
            UserSqid: "USER1",
            StartDateUtc: Now.AddDays(1),
            EndDateUtc: Now.AddDays(5),
            DelegateSqid: "USER2",
            Reason: "Concediu");
        svc.PlanAsync(body, Arg.Any<CancellationToken>())
            .Returns(Result<UserAbsenceOutputDto>.Success(dto));

        var controller = new UserAbsencesController(svc, sqids);

        var result = await controller.PlanAsync(body, CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedResult>().Subject;
        created.Value.Should().BeSameAs(dto);
        created.Location.Should().Be("/api/user-absences/ABS1");
    }

    [Fact]
    public async Task GetAsync_AfterPlan_ReturnsRow()
    {
        var svc = Substitute.For<IUserAbsenceService>();
        var sqids = Substitute.For<ISqidService>();
        sqids.TryDecode("ABS1").Returns(Result<long>.Success(7L));
        var dto = new UserAbsenceOutputDto(
            Id: "ABS1",
            UserSqid: "USER1",
            DelegateSqid: "USER2",
            StartDateUtc: Now.AddDays(1),
            EndDateUtc: Now.AddDays(5),
            Status: "Planned",
            ActivatedAtUtc: null,
            CompletedAtUtc: null,
            RoutedTaskCount: 0,
            Reason: "Concediu");
        svc.GetAsync(7L, Arg.Any<CancellationToken>()).Returns(Task.FromResult<UserAbsenceOutputDto?>(dto));

        var controller = new UserAbsencesController(svc, sqids);

        var result = await controller.GetAsync("ABS1", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task CancelAsync_HappyPath_Returns204()
    {
        var svc = Substitute.For<IUserAbsenceService>();
        var sqids = Substitute.For<ISqidService>();
        sqids.TryDecode("ABS1").Returns(Result<long>.Success(7L));
        svc.CancelAsync(7L, Arg.Any<CancellationToken>()).Returns(Result.Success());

        var controller = new UserAbsencesController(svc, sqids);

        var result = await controller.CancelAsync("ABS1", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }
}
