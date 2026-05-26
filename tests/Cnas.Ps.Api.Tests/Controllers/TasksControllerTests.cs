using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="TasksController"/>. Direct-construction pattern matching the
/// rest of the controller test suite; the <see cref="ITaskInboxService"/> dependency is
/// faked with NSubstitute. Authorization (<c>CnasDecider</c>) and rate-limiting are
/// validated through journey tests — these tests focus on the controller's branch logic
/// for list / claim / complete and the <see cref="ErrorCodes"/> → HTTP-status mapping.
/// </summary>
public sealed class TasksControllerTests
{
    /// <summary>Helper that returns a fresh service substitute.</summary>
    private static ITaskInboxService NewServiceMock() => Substitute.For<ITaskInboxService>();

    /// <summary>
    /// Builds the SUT around the supplied service. The Sqid encoder is wired with a
    /// permissive substitute so the existing claim/complete tests continue to pass —
    /// the reassign-path tests in <c>TasksControllerReassignTests</c> override
    /// per-call behaviour as needed.
    /// </summary>
    private static TasksController NewController(ITaskInboxService svc)
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string?>())
            .Returns(call => Result<long>.Success(42L));
        return new TasksController(svc, sqids);
    }

    /// <summary>Builds a representative inbox page with Sqid-encoded ids.</summary>
    private static PagedResult<TaskInboxItem> SamplePage() => new(
        Items:
        [
            new TaskInboxItem(
                Id: "TaSk1234",
                Title: "Examinare cerere pensie",
                Status: "Pending",
                DueAtUtc: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                DossierId: "DoSr5678"),
        ],
        Page: 1,
        PageSize: 20,
        TotalCount: 1);

    // ─────────────────────── ListAsync ───────────────────────

    [Fact]
    public async Task ListAsync_Success_Returns200WithPagedBody()
    {
        // Arrange — the service returns a single Sqid-encoded inbox row.
        var svc = NewServiceMock();
        var paged = SamplePage();
        svc.ListAsync(Arg.Any<PageRequest>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<TaskInboxItem>>.Success(paged));
        var controller = NewController(svc);

        // Act — defaults applied at the action signature.
        var result = await controller.ListAsync(new PageRequest(1, 20), CancellationToken.None);

        // Assert — 200 OK with the paged body passed through verbatim.
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(paged);
    }

    [Fact]
    public async Task ListAsync_PropagatesPagingToService()
    {
        // Arrange — pin that the [FromQuery] PageRequest reaches the service unchanged.
        var svc = NewServiceMock();
        svc.ListAsync(Arg.Any<PageRequest>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<TaskInboxItem>>.Success(SamplePage()));
        var controller = NewController(svc);

        // Act
        _ = await controller.ListAsync(new PageRequest(3, 50), CancellationToken.None);

        // Assert
        await svc.Received(1).ListAsync(
            Arg.Is<PageRequest>(p => p.Page == 3 && p.PageSize == 50),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAsync_Unauthorized_Returns401()
    {
        // Arrange — the service returns the documented UNAUTHORIZED failure (anonymous caller).
        var svc = NewServiceMock();
        svc.ListAsync(Arg.Any<PageRequest>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<TaskInboxItem>>.Failure(
                ErrorCodes.Unauthorized, "Not authenticated."));
        var controller = NewController(svc);

        // Act
        var result = await controller.ListAsync(new PageRequest(1, 20), CancellationToken.None);

        // Assert — 401 ProblemDetails carrying the service-layer message.
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Not authenticated.");
    }

    // ─────────────────────── ClaimAsync ───────────────────────

    [Fact]
    public async Task ClaimAsync_Success_Returns204()
    {
        // Arrange
        var svc = NewServiceMock();
        svc.ClaimAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Success());
        var controller = NewController(svc);

        // Act
        var result = await controller.ClaimAsync("TaSk1234", CancellationToken.None);

        // Assert — 204 No Content; service saw the Sqid verbatim.
        result.Should().BeOfType<NoContentResult>();
        await svc.Received(1).ClaimAsync("TaSk1234", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClaimAsync_NotFound_Returns404()
    {
        // Arrange — unknown Sqid.
        var svc = NewServiceMock();
        svc.ClaimAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.NotFound, "Task not found."));
        var controller = NewController(svc);

        // Act
        var result = await controller.ClaimAsync("missing", CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ClaimAsync_InvalidSqid_Returns400()
    {
        // Arrange — malformed Sqid surfaces from the service as INVALID_SQID.
        var svc = NewServiceMock();
        svc.ClaimAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.InvalidSqid, "bad sqid"));
        var controller = NewController(svc);

        // Act
        var result = await controller.ClaimAsync("not-a-sqid", CancellationToken.None);

        // Assert — 400 BadRequest ProblemDetails (default mapping for unknown codes).
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    // ─────────────────────── CompleteAsync ───────────────────────

    [Fact]
    public async Task CompleteAsync_Success_Returns204()
    {
        // Arrange
        var svc = NewServiceMock();
        svc.CompleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Success());
        var controller = NewController(svc);

        // Act
        var result = await controller.CompleteAsync(
            "TaSk1234",
            new CompleteTaskRequest("{\"verdict\":\"ok\"}"),
            CancellationToken.None);

        // Assert — 204 No Content; the body's ResultJson is forwarded verbatim to the service.
        result.Should().BeOfType<NoContentResult>();
        await svc.Received(1).CompleteAsync(
            Arg.Is<string>(s => s == "TaSk1234"),
            Arg.Is<string>(s => s == "{\"verdict\":\"ok\"}"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteAsync_NotAssignee_Returns403()
    {
        // Arrange — caller is not the assigned user → WORKFLOW_NOT_ASSIGNEE → 403.
        var svc = NewServiceMock();
        svc.CompleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.WorkflowNotAssignee, "Caller is not the assigned user."));
        var controller = NewController(svc);

        // Act
        var result = await controller.CompleteAsync(
            "TaSk1234",
            new CompleteTaskRequest("{}"),
            CancellationToken.None);

        // Assert — 403 Forbidden ProblemDetails carrying the service-layer message.
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Caller is not the assigned user.");
    }

    [Fact]
    public async Task CompleteAsync_NotFound_Returns404()
    {
        // Arrange
        var svc = NewServiceMock();
        svc.CompleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.NotFound, "Task not found."));
        var controller = NewController(svc);

        // Act
        var result = await controller.CompleteAsync(
            "missing",
            new CompleteTaskRequest("{}"),
            CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }
}
