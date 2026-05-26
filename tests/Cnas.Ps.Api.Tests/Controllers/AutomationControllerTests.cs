using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="AutomationController"/>. Direct-construction pattern matching
/// the rest of the suite — exercises controller branch logic with a NSubstitute mock of
/// <see cref="IAutomationService"/>.
/// </summary>
public sealed class AutomationControllerTests
{
    /// <summary>Helper that returns a fresh service substitute.</summary>
    private static IAutomationService NewServiceMock() =>
        Substitute.For<IAutomationService>();

    /// <summary>Builds the SUT around the supplied service.</summary>
    private static AutomationController NewController(IAutomationService svc) =>
        new(svc, Substitute.For<IJobStateInspector>());

    [Fact]
    public async Task RunNow_NullBody_Success_Returns202_AndForwardsEmptyJson()
    {
        // Arrange — service returns Success() for the on-demand run.
        var svc = NewServiceMock();
        svc.RunNowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Success());
        var controller = NewController(svc);

        // Act — no body supplied → controller forwards "{}".
        var result = await controller.RunNowAsync(
            "mpay-dispatcher", body: null, CancellationToken.None);

        // Assert — 202 Accepted because the service surface is fire-and-forget.
        result.Should().BeOfType<AcceptedResult>();
        await svc.Received(1).RunNowAsync(
            Arg.Is<string>(s => s == "mpay-dispatcher"),
            Arg.Is<string>(s => s == "{}"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunNow_WithParameters_SerialisesAndForwardsJson()
    {
        // Arrange
        var svc = NewServiceMock();
        svc.RunNowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Success());
        var controller = NewController(svc);

        // Act — body carries a single parameter.
        var body = new AutomationRunNowRequest(
            new Dictionary<string, string?> { ["dryRun"] = "true" });
        var result = await controller.RunNowAsync(
            "mpay-dispatcher", body, CancellationToken.None);

        // Assert — 202 Accepted; the parameter map was serialised to JSON before forwarding.
        result.Should().BeOfType<AcceptedResult>();
        await svc.Received(1).RunNowAsync(
            Arg.Is<string>(s => s == "mpay-dispatcher"),
            Arg.Is<string>(s => s.Contains("\"dryRun\"") && s.Contains("\"true\"")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunNow_ServiceFailure_Returns400()
    {
        // Arrange — service refuses (unknown automation code, validation failure, etc.).
        var svc = NewServiceMock();
        svc.RunNowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.ValidationFailed, "Bad parameters."));
        var controller = NewController(svc);

        // Act
        var result = await controller.RunNowAsync("bogus", body: null, CancellationToken.None);

        // Assert
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Bad parameters.");
    }

    [Fact]
    public async Task RunNow_NotFound_Returns404()
    {
        // Arrange — service does not recognise the automation code.
        var svc = NewServiceMock();
        svc.RunNowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.NotFound, "Unknown automation code."));
        var controller = NewController(svc);

        // Act
        var result = await controller.RunNowAsync("ghost", body: null, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Schedule_Success_Returns204()
    {
        // Arrange
        var svc = NewServiceMock();
        svc.ScheduleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Success());
        var controller = NewController(svc);

        // Act
        var result = await controller.ScheduleAsync(
            "mpay-dispatcher",
            new AutomationScheduleRequest("0 0 */1 * * ?"),
            CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        await svc.Received(1).ScheduleAsync(
            Arg.Is<string>(s => s == "mpay-dispatcher"),
            Arg.Is<string>(s => s == "0 0 */1 * * ?"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Schedule_NullBody_Throws()
    {
        var controller = NewController(NewServiceMock());
        await FluentActions.Awaiting(() =>
                controller.ScheduleAsync("mpay-dispatcher", null!, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Schedule_ValidationFailure_Returns400()
    {
        // Arrange — malformed cron expression rejected by Quartz parser.
        var svc = NewServiceMock();
        svc.ScheduleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.ValidationFailed, "Invalid cron expression."));
        var controller = NewController(svc);

        // Act
        var result = await controller.ScheduleAsync(
            "mpay-dispatcher",
            new AutomationScheduleRequest("not-a-cron"),
            CancellationToken.None);

        // Assert
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Invalid cron expression.");
    }
}
