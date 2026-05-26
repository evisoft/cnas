using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UserLayout;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0535 / CF 04.07-08 — unit tests for <see cref="UserLayoutPreferencesController"/>.
/// Direct-construction pattern (no HTTP pipeline) — the service is faked with
/// NSubstitute. Authorization + rate-limiting attributes are covered by the
/// integration-style harness tests; here we just pin the Result→ActionResult
/// mapping for each branch the controller cares about.
/// </summary>
public sealed class UserLayoutPreferencesControllerTests
{
    private static IUserLayoutPreferencesService NewSvc() =>
        Substitute.For<IUserLayoutPreferencesService>();

    private static UserLayoutPreferencesController NewController(IUserLayoutPreferencesService svc) =>
        new(svc);

    [Fact]
    public async Task GetMine_Success_Returns200WithDto()
    {
        var svc = NewSvc();
        var dto = new UserLayoutPreferencesDto(
            Grids: new Dictionary<string, GridLayoutDto>(),
            DefaultPageSize: 25,
            DashboardWidgetOrder: []);
        svc.GetForCurrentUserAsync(Arg.Any<CancellationToken>()).Returns(dto);
        var controller = NewController(svc);

        var result = await controller.GetMineAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task SaveMine_Success_Returns200WithDto()
    {
        var svc = NewSvc();
        var dto = new UserLayoutPreferencesDto(
            Grids: new Dictionary<string, GridLayoutDto>(),
            DefaultPageSize: 25,
            DashboardWidgetOrder: []);
        svc.SaveAsync(Arg.Any<UserLayoutPreferencesSaveDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<UserLayoutPreferencesDto>.Success(dto));
        var controller = NewController(svc);

        var input = new UserLayoutPreferencesSaveDto(
            Grids: new Dictionary<string, GridLayoutDto>(),
            DefaultPageSize: 25,
            DashboardWidgetOrder: []);

        var result = await controller.SaveMineAsync(input, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task SaveMine_ValidationFailure_Returns400()
    {
        var svc = NewSvc();
        svc.SaveAsync(Arg.Any<UserLayoutPreferencesSaveDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<UserLayoutPreferencesDto>.Failure(
                ErrorCodes.ValidationFailed, "DefaultPageSize must be between 10 and 200."));
        var controller = NewController(svc);

        var input = new UserLayoutPreferencesSaveDto(
            Grids: new Dictionary<string, GridLayoutDto>(),
            DefaultPageSize: 5,
            DashboardWidgetOrder: []);

        var result = await controller.SaveMineAsync(input, CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task SaveMine_NullBody_ThrowsArgumentNullException()
    {
        var controller = NewController(NewSvc());

        await FluentActions.Awaiting(() =>
                controller.SaveMineAsync(null!, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
    }
}
