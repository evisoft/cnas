using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="ApplicationsController"/> using direct construction with
/// NSubstitute mocks for the two service dependencies (<see cref="IApplicationService"/>
/// and <see cref="IApplicationProcessingService"/>). These tests deliberately avoid
/// <c>WebApplicationFactory</c> — the controller's branch logic is pure and only needs the
/// service contracts substituted.
/// </summary>
public sealed class ApplicationsControllerTests
{
    /// <summary>Builds a fresh <see cref="IApplicationService"/> substitute.</summary>
    private static IApplicationService NewAppServiceMock() => Substitute.For<IApplicationService>();

    /// <summary>Builds a fresh <see cref="IApplicationProcessingService"/> substitute.</summary>
    private static IApplicationProcessingService NewProcessingMock() => Substitute.For<IApplicationProcessingService>();

    /// <summary>Builds the SUT around the supplied services. A <c>null</c> processing service is replaced with a fresh substitute so withdraw / submit tests do not have to construct one.</summary>
    private static ApplicationsController NewController(
        IApplicationService apps,
        IApplicationProcessingService? processing = null)
        => new(apps, processing ?? NewProcessingMock());

    /// <summary>Convenience builder for a deterministic successful application output.</summary>
    private static ApplicationOutput SuccessOutput(string id = "abc12")
        => new(id, "Submitted", "PS-2026-0001", new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task SubmitAsync_ServiceReturnsSuccess_Returns201_WithLocation()
    {
        // Arrange: service returns a valid ApplicationOutput.
        var svc = NewAppServiceMock();
        var output = SuccessOutput("abc12");
        svc.SubmitAsync(Arg.Any<SubmitApplicationInput>(), Arg.Any<CancellationToken>())
           .Returns(Result<ApplicationOutput>.Success(output));
        var controller = NewController(svc);
        var input = new SubmitApplicationInput("ssp12345", "{}", Array.Empty<string>());

        // Act
        var result = await controller.SubmitAsync(input, CancellationToken.None);

        // Assert: 201 Created pointing at GetAsync with the new id in route values.
        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.ActionName.Should().Be(nameof(ApplicationsController.GetAsync));
        created.RouteValues.Should().NotBeNull();
        created.RouteValues!["id"].Should().Be("abc12");
        created.Value.Should().BeSameAs(output);
    }

    [Fact]
    public async Task SubmitAsync_ServiceReturnsFailure_Returns400_WithProblemDetails()
    {
        // Arrange: service returns a generic failure (unknown error code maps to 400 default).
        var svc = NewAppServiceMock();
        svc.SubmitAsync(Arg.Any<SubmitApplicationInput>(), Arg.Any<CancellationToken>())
           .Returns(Result<ApplicationOutput>.Failure("VALIDATION_ERROR", "bad payload"));
        var controller = NewController(svc);
        var input = new SubmitApplicationInput("ssp12345", "{}", Array.Empty<string>());

        // Act
        var result = await controller.SubmitAsync(input, CancellationToken.None);

        // Assert: ASP.NET wraps Problem() in ObjectResult carrying ProblemDetails with status 400.
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("bad payload");
    }

    [Fact]
    public async Task MineAsync_ServiceReturnsSuccess_ReturnsOkWithPagedResult()
    {
        // Arrange: service returns a single-page result.
        var svc = NewAppServiceMock();
        var paged = new PagedResult<ApplicationListItemOutput>(
            new[]
            {
                new ApplicationListItemOutput("abc12", "Submitted", "PS-1", "usr1", DateTime.UtcNow),
            },
            Page: 1,
            PageSize: 20,
            TotalCount: 1);
        svc.MineAsync(Arg.Any<PageRequest>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<ApplicationListItemOutput>>.Success(paged));
        var controller = NewController(svc);

        // Act
        var result = await controller.MineAsync(page: 1, pageSize: 20, cancellationToken: CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(paged);
    }

    [Fact]
    public async Task MineAsync_ServiceReturnsForbidden_Returns403_AfterBug021Fix()
    {
        // Arrange: service signals Forbidden. After BUG-021 the controller maps this to 403,
        // not 400 — this test pins the new behaviour for MineAsync as well.
        var svc = NewAppServiceMock();
        svc.MineAsync(Arg.Any<PageRequest>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<ApplicationListItemOutput>>.Failure(ErrorCodes.Forbidden, "no access"));
        var controller = NewController(svc);

        // Act
        var result = await controller.MineAsync(page: 1, pageSize: 20, cancellationToken: CancellationToken.None);

        // Assert
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("no access");
    }

    [Fact]
    public async Task GetAsync_ServiceReturnsSuccess_ReturnsOk()
    {
        // Arrange: service returns a known application.
        var svc = NewAppServiceMock();
        var output = SuccessOutput("abc12");
        svc.GetAsync(Arg.Is<string>(s => s == "abc12"), Arg.Any<CancellationToken>())
           .Returns(Result<ApplicationOutput>.Success(output));
        var controller = NewController(svc);

        // Act
        var result = await controller.GetAsync("abc12", CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(output);
    }

    [Fact]
    public async Task GetAsync_ServiceReturnsNotFound_Returns404()
    {
        // Arrange: service signals the application id is unknown.
        var svc = NewAppServiceMock();
        svc.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<ApplicationOutput>.Failure("NOT_FOUND", "missing"));
        var controller = NewController(svc);

        // Act
        var result = await controller.GetAsync("zzz99", CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task WithdrawAsync_ServiceReturnsSuccess_Returns204NoContent()
    {
        // Arrange: service confirms successful withdrawal.
        var svc = NewAppServiceMock();
        svc.WithdrawAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Success());
        var controller = NewController(svc);

        // Act
        var result = await controller.WithdrawAsync("abc12", CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task WithdrawAsync_ServiceReturnsForbidden_Returns403_Bug021Fix()
    {
        // BUG-021 regression lock: Forbidden must map to 403, not the legacy 400.
        var svc = NewAppServiceMock();
        svc.WithdrawAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.Forbidden, "Not your application."));
        var controller = NewController(svc);

        // Act
        var result = await controller.WithdrawAsync("abc12", CancellationToken.None);

        // Assert — 403 ProblemDetails with the service's message surfaced.
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Not your application.");
    }

    [Fact]
    public async Task WithdrawAsync_ServiceReturnsNotFound_Returns404_Bug021Fix()
    {
        // BUG-021 regression lock: NotFound must map to 404, not the legacy 400.
        var svc = NewAppServiceMock();
        svc.WithdrawAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.NotFound, "Application not found."));
        var controller = NewController(svc);

        // Act
        var result = await controller.WithdrawAsync("abc12", CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task WithdrawAsync_ServiceReturnsApplicationLocked_Returns409_Bug021Fix()
    {
        // BUG-021 regression lock: ApplicationLocked (already in final state) maps to 409 Conflict.
        var svc = NewAppServiceMock();
        svc.WithdrawAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.ApplicationLocked, "Application already final."));
        var controller = NewController(svc);

        // Act
        var result = await controller.WithdrawAsync("abc12", CancellationToken.None);

        // Assert
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Application already final.");
    }

    [Fact]
    public async Task AdvanceAsync_ServiceReturnsSuccess_Returns204NoContent()
    {
        // Arrange — UC21 happy path: service advances the application and returns Success().
        var processing = NewProcessingMock();
        processing.AdvanceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(Result.Success());
        var controller = NewController(NewAppServiceMock(), processing);

        // Act
        var result = await controller.AdvanceAsync("abc12", CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        await processing.Received(1).AdvanceAsync(
            Arg.Is<string>(s => s == "abc12"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceAsync_ServiceReturnsNotFound_Returns404()
    {
        // Arrange — application or passport missing.
        var processing = NewProcessingMock();
        processing.AdvanceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(Result.Failure(ErrorCodes.NotFound, "Application not found."));
        var controller = NewController(NewAppServiceMock(), processing);

        // Act
        var result = await controller.AdvanceAsync("missing", CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task AdvanceAsync_ServiceReturnsApplicationNotSubmitted_Returns409()
    {
        // Arrange — non-Submitted application cannot be advanced through this path.
        var processing = NewProcessingMock();
        processing.AdvanceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(Result.Failure(
                      ErrorCodes.ApplicationNotSubmitted,
                      "Application not in Submitted state."));
        var controller = NewController(NewAppServiceMock(), processing);

        // Act
        var result = await controller.AdvanceAsync("abc12", CancellationToken.None);

        // Assert — 409 Conflict per StatusForCode mapping.
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }
}
