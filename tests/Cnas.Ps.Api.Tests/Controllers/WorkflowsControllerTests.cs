using System.Text.Json;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="WorkflowsController"/>. Direct-construction pattern matching
/// the rest of the suite — exercises controller branch logic with a NSubstitute mock of
/// <see cref="IWorkflowConfigurationService"/>.
/// </summary>
public sealed class WorkflowsControllerTests
{
    /// <summary>Helper that returns a fresh service substitute.</summary>
    private static IWorkflowConfigurationService NewServiceMock() =>
        Substitute.For<IWorkflowConfigurationService>();

    /// <summary>Builds the SUT around the supplied service.</summary>
    private static WorkflowsController NewController(IWorkflowConfigurationService svc) =>
        new(svc);

    [Fact]
    public async Task GetDefinition_Success_Returns200WithJsonContent()
    {
        // Arrange — the service returns a canonical workflow definition JSON.
        const string json = "{\"states\":[\"submitted\",\"approved\"]}";
        var svc = NewServiceMock();
        svc.GetDefinitionAsync("WF-AGE", Arg.Any<CancellationToken>())
           .Returns(Result<string>.Success(json));
        var controller = NewController(svc);

        // Act
        var result = await controller.GetDefinitionAsync("WF-AGE", CancellationToken.None);

        // Assert — ContentResult with application/json content-type and verbatim body.
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("application/json");
        content.Content.Should().Be(json);
    }

    [Fact]
    public async Task GetDefinition_NotFound_Returns404()
    {
        // Arrange — the workflow code is unknown.
        var svc = NewServiceMock();
        svc.GetDefinitionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<string>.Failure(ErrorCodes.NotFound, "Unknown workflow code."));
        var controller = NewController(svc);

        // Act
        var result = await controller.GetDefinitionAsync("BOGUS", CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task SaveDefinition_Success_Returns204()
    {
        // Arrange — service accepts the definition.
        var svc = NewServiceMock();
        svc.SaveDefinitionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Success());
        var controller = NewController(svc);
        using var json = JsonDocument.Parse("{\"version\":2}");

        // Act
        var result = await controller.SaveDefinitionAsync(
            "WF-AGE", json.RootElement, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        // Verify the service saw the canonical raw JSON text (re-serialised from the element).
        await svc.Received(1).SaveDefinitionAsync(
            Arg.Is<string>(s => s == "WF-AGE"),
            Arg.Is<string>(s => s.Contains("\"version\"")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveDefinition_ValidationFailed_Returns400()
    {
        // Arrange — service rejects malformed workflow body.
        var svc = NewServiceMock();
        svc.SaveDefinitionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.ValidationFailed, "Missing required state."));
        var controller = NewController(svc);
        using var json = JsonDocument.Parse("{}");

        // Act
        var result = await controller.SaveDefinitionAsync(
            "WF-AGE", json.RootElement, CancellationToken.None);

        // Assert
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Missing required state.");
    }

    [Fact]
    public async Task SaveDefinition_NotFoundOnService_Returns404()
    {
        // Arrange — service maps unknown workflow code on save to NotFound (e.g. when
        // requiring a pre-registered code). The controller surfaces NotFound() either way.
        var svc = NewServiceMock();
        svc.SaveDefinitionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.NotFound, "Unknown workflow code."));
        var controller = NewController(svc);
        using var json = JsonDocument.Parse("{}");

        // Act
        var result = await controller.SaveDefinitionAsync(
            "BOGUS", json.RootElement, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }
}
