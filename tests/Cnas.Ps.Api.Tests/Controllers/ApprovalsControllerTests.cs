using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0590 / TOR CF 10.01 — unit tests for <see cref="ApprovalsController"/>.
/// Mirrors the direct-construction pattern of the rest of the controller suite;
/// authorization is asserted indirectly through the <c>[Authorize(Policy=...)]</c>
/// attribute on the controller type — the policy registration itself is covered
/// by <c>RolePoliciesTests</c>. These tests focus on the controller's branch
/// logic + the <see cref="ErrorCodes"/> → HTTP status mapping.
/// </summary>
public sealed class ApprovalsControllerTests
{
    /// <summary>Returns a fresh service substitute.</summary>
    private static IApprovalWorkspaceService NewServiceMock()
        => Substitute.For<IApprovalWorkspaceService>();

    /// <summary>Builds the SUT around the supplied service substitute.</summary>
    /// <param name="svc">The service to inject.</param>
    private static ApprovalsController NewController(IApprovalWorkspaceService svc)
        => new(svc);

    // ─────────────────────── GetSummaryAsync ───────────────────────

    [Fact]
    public async Task GetSummaryAsync_Success_Returns200WithSummaryBody()
    {
        var svc = NewServiceMock();
        var summary = new ApprovalWorkspaceSummaryDto(PendingCount: 7, OverdueCount: 2, TodayCount: 3);
        svc.GetSummaryAsync(Arg.Any<CancellationToken>())
           .Returns(Result<ApprovalWorkspaceSummaryDto>.Success(summary));
        var controller = NewController(svc);

        var result = await controller.GetSummaryAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(summary);
    }

    [Fact]
    public async Task GetSummaryAsync_Failure_MapsToProblemDetails()
    {
        var svc = NewServiceMock();
        svc.GetSummaryAsync(Arg.Any<CancellationToken>())
           .Returns(Result<ApprovalWorkspaceSummaryDto>.Failure(
                ErrorCodes.Internal, "read failed"));
        var controller = NewController(svc);

        var result = await controller.GetSummaryAsync(CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
            .Which.Detail.Should().Be("read failed");
    }

    // ─────────────────────── ListPendingAsync ───────────────────────

    [Fact]
    public async Task ListPendingAsync_Success_Returns200WithPagedBody()
    {
        var svc = NewServiceMock();
        var page = new PagedResult<ApprovalQueueItemDto>(
            Items: new[]
            {
                new ApprovalQueueItemDto(
                    Id: "SqId1",
                    DossierCode: "D-2026-0001",
                    DecisionTitle: "Pensie pentru limită de vârstă",
                    ExaminerName: "Maria Examinator",
                    ExaminerSqid: "ExSq1",
                    EmittedAtUtc: new DateTime(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc),
                    SlaDeadlineUtc: new DateTime(2026, 5, 29, 9, 0, 0, DateTimeKind.Utc)),
            },
            Page: 1, PageSize: 20, TotalCount: 1);
        svc.ListPendingAsync(1, 20, Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<ApprovalQueueItemDto>>.Success(page));
        var controller = NewController(svc);

        var result = await controller.ListPendingAsync(1, 20, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(page);
    }

    [Fact]
    public async Task ListPendingAsync_PropagatesPagingToService()
    {
        var svc = NewServiceMock();
        svc.ListPendingAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<ApprovalQueueItemDto>>.Success(
                new PagedResult<ApprovalQueueItemDto>(
                    Array.Empty<ApprovalQueueItemDto>(), Page: 3, PageSize: 50, TotalCount: 0)));
        var controller = NewController(svc);

        _ = await controller.ListPendingAsync(page: 3, pageSize: 50, CancellationToken.None);

        await svc.Received(1).ListPendingAsync(3, 50, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListPendingAsync_ServiceFails_Returns400ProblemDetails()
    {
        var svc = NewServiceMock();
        svc.ListPendingAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<ApprovalQueueItemDto>>.Failure(
                ErrorCodes.Internal, "boom"));
        var controller = NewController(svc);

        var result = await controller.ListPendingAsync(1, 20, CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
            .Which.Detail.Should().Be("boom");
    }

    // ─────────────────────── Authorisation contract ───────────────────────

    [Fact]
    public void Controller_RequiresCnasDeciderPolicy()
    {
        // Pin the [Authorize(Policy = ...)] attribute the controller declares so a
        // future refactor cannot silently widen the policy gate. The non-decider
        // 403 behaviour itself is enforced by the ASP.NET pipeline; here we just
        // assert the policy name carried by the attribute.
        var attr = typeof(ApprovalsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();
        attr.Policy.Should().Be(AuthorizationComposition.CnasDecider,
            "the approval workspace must be gated by the cnas-decider policy");
    }
}
