using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0127 / CF 16.11 — controller-side tests for the per-task reassignment surface on
/// <see cref="TasksController"/>.
/// </summary>
public sealed class TasksControllerReassignTests
{
    [Fact]
    public async Task ReassignAsync_HappyPath_Returns200WithDto()
    {
        var svc = Substitute.For<ITaskInboxService>();
        var sqids = Substitute.For<ISqidService>();
        sqids.TryDecode("TASK1").Returns(Result<long>.Success(42L));
        sqids.TryDecode("USER2").Returns(Result<long>.Success(200L));

        var dto = new WorkflowTaskOutputDto(
            Id: "TASK1",
            Title: "Examinare",
            Status: "InProgress",
            AssigneeSqid: "USER2",
            OriginalAssigneeSqid: "USER1",
            DelegatedFromAbsenceSqid: null,
            ReassignmentCount: 1,
            ReassignmentReason: "Concediu medical");
        svc.ReassignAsync(42L, 200L, "Concediu medical", null, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTaskOutputDto>.Success(dto));

        var controller = new TasksController(svc, sqids);
        var body = new WorkflowTaskReassignDto("USER2", "Concediu medical");

        var result = await controller.ReassignAsync("TASK1", body, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task ReassignAsync_ShortReason_Returns400()
    {
        var svc = Substitute.For<ITaskInboxService>();
        var sqids = Substitute.For<ISqidService>();
        var controller = new TasksController(svc, sqids);
        var body = new WorkflowTaskReassignDto("USER2", "x"); // too short

        var result = await controller.ReassignAsync("TASK1", body, CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task RevertReassignmentAsync_HappyPath_Returns204()
    {
        var svc = Substitute.For<ITaskInboxService>();
        var sqids = Substitute.For<ISqidService>();
        sqids.TryDecode("TASK1").Returns(Result<long>.Success(42L));
        svc.RevertReassignmentAsync(42L, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var controller = new TasksController(svc, sqids);

        var result = await controller.RevertReassignmentAsync("TASK1", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }
}
