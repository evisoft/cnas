using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.WorkflowNotifications;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0128 / R0173 — unit tests for <see cref="WorkflowNotificationStrategiesController"/>.
/// Direct-construction pattern matching the rest of the suite — exercises controller
/// branch logic with NSubstitute mocks of <see cref="IWorkflowNotificationStrategyService"/>.
/// </summary>
public sealed class WorkflowNotificationStrategiesControllerTests
{
    private static readonly IReadOnlyList<string> EmailInAppChannels = new[] { "Email", "InApp" };
    private static readonly IReadOnlyList<string> EmailOnlyChannels = new[] { "Email" };
    private static readonly IReadOnlyList<string> AssigneeRole = new[] { "Assignee" };
    private static readonly IReadOnlyList<string> BogusRole = new[] { "BogusRole" };

    [Fact]
    public async Task Upsert_Success_Returns200WithDto_AndSqidRoundTripsWorkflowId()
    {
        // Arrange — the service echoes back a DTO carrying the workflow Sqid.
        const string workflowSqid = "SQID-WF-42";
        const string eventCode = "Task.Assigned";
        var dto = new WorkflowNotificationStrategyOutput(
            Id: "SQID-99",
            WorkflowDefinitionId: workflowSqid,
            EventCode: eventCode,
            IsEnabled: true,
            Channels: EmailInAppChannels,
            RecipientRoles: AssigneeRole,
            TemplateCodeOverride: null,
            QuietHoursStart: null,
            QuietHoursEnd: null,
            Description: "test");

        var svc = Substitute.For<IWorkflowNotificationStrategyService>();
        svc.UpsertAsync(workflowSqid, eventCode, Arg.Any<WorkflowNotificationStrategyUpsertInput>(),
            Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowNotificationStrategyOutput>.Success(dto));

        var controller = new WorkflowNotificationStrategiesController(svc);
        var input = new WorkflowNotificationStrategyUpsertInput(
            IsEnabled: true,
            Channels: EmailInAppChannels,
            RecipientRoles: AssigneeRole,
            TemplateCodeOverride: null,
            QuietHoursStart: null,
            QuietHoursEnd: null,
            Description: "test");

        // Act
        var result = await controller.UpsertAsync(workflowSqid, eventCode, input, CancellationToken.None);

        // Assert
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<WorkflowNotificationStrategyOutput>().Subject;
        body.WorkflowDefinitionId.Should().Be(workflowSqid);
        body.EventCode.Should().Be(eventCode);
    }

    [Fact]
    public async Task Upsert_ValidationFailed_Returns400ProblemDetails()
    {
        var svc = Substitute.For<IWorkflowNotificationStrategyService>();
        svc.UpsertAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<WorkflowNotificationStrategyUpsertInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowNotificationStrategyOutput>.Failure(
                ErrorCodes.ValidationFailed, "RecipientRoles invalid."));

        var controller = new WorkflowNotificationStrategiesController(svc);
        var input = new WorkflowNotificationStrategyUpsertInput(
            true,
            EmailOnlyChannels,
            BogusRole,
            null, null, null, null);

        var result = await controller.UpsertAsync("SQID-1", "Task.Assigned", input, CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Disable_NotFound_Returns404()
    {
        var svc = Substitute.For<IWorkflowNotificationStrategyService>();
        svc.DisableAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorCodes.NotFound, "Strategy not found."));

        var controller = new WorkflowNotificationStrategiesController(svc);

        var result = await controller.DisableAsync("SQID-1", "Task.Assigned", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }
}
