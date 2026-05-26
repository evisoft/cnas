using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0191 / TOR SEC 050 / TOR ARH 028 — tests for
/// <see cref="AdminHistoryController"/>. Verifies the controller is gated by
/// the CnasAdmin policy and maps the service-layer Result envelope to the
/// expected HTTP status codes.
/// </summary>
public sealed class AdminHistoryControllerTests
{
    [Fact]
    public void Controller_RequiresCnasAdminPolicy()
    {
        var attr = typeof(AdminHistoryController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(AuthorizationComposition.CnasAdmin);
    }

    [Fact]
    public async Task Get_ServiceSuccess_Returns200WithTimeline()
    {
        var svc = Substitute.For<IEntityHistoryService>();
        var timeline = new EntityHistoryTimelineDto(
            EntityType: "UserProfile",
            EntitySqid: "SQID-7",
            Rows: new List<EntityHistoryRowDto>
            {
                new(Id: "SQID-1", EntityType: "UserProfile", EntitySqid: "SQID-7",
                    ChangedAtUtc: new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc),
                    Operation: "I", PayloadJson: "{}", ActorSqid: "SQID-ADMIN"),
            });
        svc.GetHistoryAsync("UserProfile", "SQID-7", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<EntityHistoryTimelineDto>.Success(timeline)));

        var controller = new AdminHistoryController(svc);
        var result = await controller.GetAsync("UserProfile", "SQID-7", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(timeline);
    }

    [Fact]
    public async Task Get_InvalidSqid_Returns400()
    {
        var svc = Substitute.For<IEntityHistoryService>();
        svc.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<EntityHistoryTimelineDto>.Failure(
                ErrorCodes.InvalidSqid, "bad sqid")));

        var controller = new AdminHistoryController(svc);
        var result = await controller.GetAsync("UserProfile", "@@@", CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}
