using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Integration;

/// <summary>
/// R0381 / UC05 — controller-level integration coverage for the supervisor surface
/// on <see cref="TasksController"/>. Two scenarios pin the contract: listing the
/// team queue routes through <see cref="ITaskInboxService.ListTeamQueueAsync"/>
/// and surfaces 200 with the paged DTO; reassigning via the existing reassign
/// route routes through <see cref="ITaskInboxService.ReassignAsync"/> and returns
/// 200 with the updated DTO.
/// </summary>
/// <remarks>
/// <para>
/// The fuller end-to-end (HTTP-host) journey lives in
/// <c>Cnas.Ps.E2E.Tests/Journeys/Uc05_TaskInboxJourneyTests.cs</c>; this file
/// covers the in-process binding through the controller as the smaller-step
/// regression net (no WebApplicationFactory required, runs in &lt; 100 ms).
/// </para>
/// </remarks>
public sealed class TaskInboxJourneyTests
{
    [Fact]
    public async Task SupervisorListTeam_HappyPath_Returns200WithPagedDto()
    {
        // Arrange — service returns two team rows for the supervisor's queue.
        var svc = Substitute.For<ITaskInboxService>();
        var sqids = Substitute.For<ISqidService>();

        var paged = new PagedResult<SupervisorTeamTaskDto>(
            Items: new[]
            {
                new SupervisorTeamTaskDto(
                    Id: "t1",
                    Title: "Examinare dosar",
                    Status: "Pending",
                    DueAtUtc: new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
                    DossierId: "d1",
                    AssigneeSqid: "u101",
                    AssigneeDisplayName: "Ion Popescu"),
                new SupervisorTeamTaskDto(
                    Id: "t2",
                    Title: "Examinare dosar (al doilea)",
                    Status: "InProgress",
                    DueAtUtc: new DateTime(2026, 6, 2, 9, 0, 0, DateTimeKind.Utc),
                    DossierId: "d2",
                    AssigneeSqid: "u102",
                    AssigneeDisplayName: "Maria Ionescu"),
            },
            Page: 1, PageSize: 20, TotalCount: 2);

        svc.ListTeamQueueAsync(null, 1, 20, Arg.Any<CancellationToken>())
            .Returns(Result<PagedResult<SupervisorTeamTaskDto>>.Success(paged));

        var controller = new TasksController(svc, sqids);

        // Act
        var result = await controller.ListTeamQueueAsync(
            assignee: null, page: 1, pageSize: 20, CancellationToken.None);

        // Assert — 200 OK with the exact DTO instance the service returned.
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(paged);
    }

    [Fact]
    public async Task SupervisorReassign_HappyPath_Returns200WithUpdatedTaskDto()
    {
        // Arrange — supervisor reassigns task T to user U2 with a valid reason.
        var svc = Substitute.For<ITaskInboxService>();
        var sqids = Substitute.For<ISqidService>();
        sqids.TryDecode("TASK1").Returns(Result<long>.Success(42L));
        sqids.TryDecode("USER2").Returns(Result<long>.Success(200L));

        var updated = new WorkflowTaskOutputDto(
            Id: "TASK1",
            Title: "Examinare",
            Status: "InProgress",
            AssigneeSqid: "USER2",
            OriginalAssigneeSqid: "USER1",
            DelegatedFromAbsenceSqid: null,
            ReassignmentCount: 1,
            ReassignmentReason: "Director's override");
        svc.ReassignAsync(42L, 200L, "Director's override", null, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTaskOutputDto>.Success(updated));

        var controller = new TasksController(svc, sqids);
        var body = new WorkflowTaskReassignDto("USER2", "Director's override");

        // Act — exercises the same /api/tasks/{sqid}/reassign route the
        // SupervisorWorkspace page POSTs to.
        var result = await controller.ReassignAsync("TASK1", body, CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(updated);
    }

    [Fact]
    public async Task SupervisorListTeam_InvalidSqidFilter_Returns400()
    {
        // Arrange — service surfaces an InvalidSqid failure; controller maps to 400.
        var svc = Substitute.For<ITaskInboxService>();
        var sqids = Substitute.For<ISqidService>();
        svc.ListTeamQueueAsync("BAD", 1, 20, Arg.Any<CancellationToken>())
            .Returns(Result<PagedResult<SupervisorTeamTaskDto>>.Failure(
                ErrorCodes.InvalidSqid, "bad sqid"));

        var controller = new TasksController(svc, sqids);

        // Act
        var result = await controller.ListTeamQueueAsync(
            assignee: "BAD", page: 1, pageSize: 20, CancellationToken.None);

        // Assert — 400 ProblemDetails (mapped via the controller's status table).
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}
