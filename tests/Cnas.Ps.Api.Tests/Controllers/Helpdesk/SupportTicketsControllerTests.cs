using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers.Helpdesk;
using Cnas.Ps.Application.Helpdesk;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers.Helpdesk;

/// <summary>
/// R2500 / TOR PIR 020-023 — tests for <see cref="SupportTicketsController"/>.
/// </summary>
public sealed class SupportTicketsControllerTests
{
    private static SupportTicketDto NewDto(string id = "SQID-1", string status = "Submitted") => new(
        Id: id,
        TicketNumber: "TKT-2026-000001",
        CategoryCode: "AUTH",
        Title: "Cannot login",
        Description: "body",
        Severity: "Normal",
        Status: status,
        SubmittedByUserSqid: "SQID-1",
        AssignedToUserSqid: null,
        SubmittedAt: new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc),
        FirstAcknowledgedAt: null,
        ResolvedAt: null,
        ClosedAt: null,
        FirstResponseDueAt: new DateTime(2026, 5, 23, 13, 0, 0, DateTimeKind.Utc),
        ResolutionDueAt: new DateTime(2026, 5, 23, 20, 0, 0, DateTimeKind.Utc),
        EscalatedAt: null,
        EscalationReason: null,
        ResolutionSummary: null,
        CancelReason: null,
        Comments: Array.Empty<SupportTicketCommentDto>(),
        SlaEvents: Array.Empty<SupportTicketSlaEventDto>());

    [Fact]
    public async Task Submit_HappyPath_Returns_201()
    {
        var svc = Substitute.For<ISupportTicketService>();
        svc.SubmitAsync(Arg.Any<SupportTicketSubmitInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<SupportTicketDto>.Success(NewDto())));
        var controller = new SupportTicketsController(svc);

        var input = new SupportTicketSubmitInputDto("AUTH", "Cannot login", "Account locked.");
        var result = await controller.SubmitAsync(input, CancellationToken.None);

        var status = result.Result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(201);
        status.Value.Should().BeOfType<SupportTicketDto>();
    }

    [Fact]
    public async Task Resolve_HappyPath_Returns_200()
    {
        var svc = Substitute.For<ISupportTicketService>();
        svc.ResolveAsync("SQID-1", Arg.Any<SupportTicketResolutionInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<SupportTicketDto>.Success(NewDto(status: "Resolved"))));
        var controller = new SupportTicketsController(svc);

        var result = await controller.ResolveAsync(
            "SQID-1",
            new SupportTicketResolutionInputDto("All clear."),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<SupportTicketDto>().Subject;
        dto.Status.Should().Be("Resolved");
    }

    [Fact]
    public async Task GetById_HappyPath_Returns_200()
    {
        var dto = NewDto();
        var svc = Substitute.For<ISupportTicketService>();
        svc.GetByIdAsync("SQID-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<SupportTicketDto>.Success(dto)));
        var controller = new SupportTicketsController(svc);

        var result = await controller.GetByIdAsync("SQID-1", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }
}
