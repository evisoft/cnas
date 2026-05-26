using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="FormsController"/> using direct construction with a
/// NSubstitute mock of <see cref="IFormIntakeService"/>. Mirrors the approach used by
/// <c>ContributorsControllerTests</c>: validates controller branch logic without
/// booting the full HTTP pipeline.
/// </summary>
public sealed class FormsControllerTests
{
    /// <summary>Helper that produces a fresh service substitute.</summary>
    private static IFormIntakeService NewServiceMock() => Substitute.For<IFormIntakeService>();

    /// <summary>Default valid request payload used across happy/failure tests.</summary>
    private static FormValidationRequest SampleRequest() =>
        new(ServicePassportId: "PP12345", FormPayloadJson: "{\"idnp\":\"2000000000006\"}");

    [Fact]
    public async Task Validate_Success_Returns200()
    {
        // Arrange
        var svc = NewServiceMock();
        svc.ValidateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Success());
        var controller = new FormsController(svc);

        // Act
        var result = await controller.ValidateAsync(SampleRequest(), CancellationToken.None);

        // Assert: empty 200 OK body.
        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task Validate_NotFound_Returns404()
    {
        // Arrange: service signals the passport is missing / disabled.
        var svc = NewServiceMock();
        svc.ValidateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.NotFound, "Service passport not found."));
        var controller = new FormsController(svc);

        // Act
        var result = await controller.ValidateAsync(SampleRequest(), CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Validate_BadRequest_Returns400()
    {
        // Arrange: validation failure carrying a human-readable detail.
        var svc = NewServiceMock();
        svc.ValidateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(
                ErrorCodes.ValidationFailed,
                "Form payload invalid: idnp is required"));
        var controller = new FormsController(svc);

        // Act
        var result = await controller.ValidateAsync(SampleRequest(), CancellationToken.None);

        // Assert: ProblemDetails carrying the failure message.
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Form payload invalid: idnp is required");
    }

    [Fact]
    public async Task Validate_InvalidSqidFromService_Returns400()
    {
        // Arrange: Sqid decode failure surfaces from the service.
        var svc = NewServiceMock();
        svc.ValidateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.InvalidSqid, "Identifier failed to decode."));
        var controller = new FormsController(svc);

        // Act
        var result = await controller.ValidateAsync(SampleRequest(), CancellationToken.None);

        // Assert
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Identifier failed to decode.");
    }

    [Fact]
    public async Task Validate_InternalError_Returns500()
    {
        // Arrange: corrupt schema or other server-side fault.
        var svc = NewServiceMock();
        svc.ValidateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.Internal, "Service passport form schema is corrupt"));
        var controller = new FormsController(svc);

        // Act
        var result = await controller.ValidateAsync(SampleRequest(), CancellationToken.None);

        // Assert: 500 with a generic message — the schema-corruption detail must NOT leak.
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(500);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().NotContain("schema is corrupt");
    }
}
