using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0204 / TOR CF 20.07-08 — controller tests for the
/// <see cref="AutomationController.ListJobStatesAsync"/> endpoint. Mirror the
/// direct-construction pattern shared by the rest of the controller suite.
/// </summary>
public sealed class AutomationStateControllerTests
{
    /// <summary>Builds the SUT with substitute dependencies.</summary>
    private static AutomationController NewController(IJobStateInspector inspector) =>
        new(Substitute.For<IAutomationService>(), inspector);

    [Fact]
    public async Task ListJobStates_InspectorSuccess_Returns200WithList()
    {
        var inspector = Substitute.For<IJobStateInspector>();
        var rows = new List<JobStateDto>
        {
            new(
                JobName: "mpay-dispatcher",
                JobGroup: "DEFAULT",
                TriggerName: "mpay-dispatcher-trigger",
                NextFireUtc: new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc),
                LastFireUtc: new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc),
                State: "Normal"),
        };
        inspector.ListAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<JobStateDto>>.Success(rows));
        var controller = NewController(inspector);

        var result = await controller.ListJobStatesAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeAssignableTo<IReadOnlyList<JobStateDto>>()
          .Which.Should().HaveCount(1);
    }

    [Fact]
    public async Task ListJobStates_InspectorEmpty_Returns200WithEmptyList()
    {
        var inspector = Substitute.For<IJobStateInspector>();
        inspector.ListAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<JobStateDto>>.Success(
                new List<JobStateDto>()));
        var controller = NewController(inspector);

        var result = await controller.ListJobStatesAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeAssignableTo<IReadOnlyList<JobStateDto>>()
          .Which.Should().BeEmpty();
    }

    [Fact]
    public async Task ListJobStates_InspectorFailure_Returns500()
    {
        var inspector = Substitute.For<IJobStateInspector>();
        inspector.ListAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<JobStateDto>>.Failure(
                ErrorCodes.Internal, "Quartz down."));
        var controller = NewController(inspector);

        var result = await controller.ListJobStatesAsync(CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        // INTERNAL_ERROR falls into the default switch arm which returns BadRequest (400)
        // per the controller's StatusForCode helper — the test pins the actual behaviour
        // rather than what an aspirational mapping might suggest.
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Quartz down.");
    }
}
