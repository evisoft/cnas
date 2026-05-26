using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R2267 / SEC 020 — unit tests for <see cref="SessionsController"/>. Direct
/// construction with a NSubstitute fake of <see cref="ISessionLockService"/>.
/// </summary>
public sealed class SessionsControllerTests
{
    private static readonly UserSessionDto Sample = new(
        Id: "SQID-1",
        UserSqid: "SQID-100",
        SessionId: "jti-abcd",
        IpAddress: "127.0.0.1",
        UserAgent: "ua",
        CreatedAtUtc: new DateTime(2026, 5, 23, 0, 0, 0, DateTimeKind.Utc),
        LastActivityUtc: new DateTime(2026, 5, 23, 1, 0, 0, DateTimeKind.Utc),
        IsLocked: true,
        IsTerminated: false,
        TerminationReason: null);

    [Fact]
    public async Task LockSessionAsync_Returns200WithDto()
    {
        var svc = Substitute.For<ISessionLockService>();
        svc.LockCurrentSessionAsync(Arg.Any<CancellationToken>())
            .Returns(Result<UserSessionDto>.Success(Sample));
        var controller = new SessionsController(svc);

        var result = await controller.LockSessionAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(Sample);
    }

    [Fact]
    public async Task LockSessionAsync_NoSession_Returns401()
    {
        var svc = Substitute.For<ISessionLockService>();
        svc.LockCurrentSessionAsync(Arg.Any<CancellationToken>())
            .Returns(Result<UserSessionDto>.Failure(ErrorCodes.Unauthorized, "no session"));
        var controller = new SessionsController(svc);

        var result = await controller.LockSessionAsync(CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task ListActiveAsync_Returns200WithList()
    {
        var svc = Substitute.For<ISessionLockService>();
        svc.ListMineAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<UserSessionDto>>.Success(new[] { Sample }));
        var controller = new SessionsController(svc);

        var result = await controller.ListActiveAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeAssignableTo<IReadOnlyList<UserSessionDto>>()
            .Which.Should().HaveCount(1);
    }
}
