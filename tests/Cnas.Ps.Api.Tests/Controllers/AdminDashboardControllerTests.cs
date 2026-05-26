using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.AdminDashboard;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0537 / CF 04.10 — tests for <see cref="AdminDashboardController"/>. Validates the
/// authorize-policy gate (cnas-admin only), the success / failure mapping over
/// <see cref="IAdminDashboardService"/>, and that the controller forwards
/// cancellation tokens to the service.
/// </summary>
public sealed class AdminDashboardControllerTests
{
    private static IAdminDashboardService NewSvc() =>
        Substitute.For<IAdminDashboardService>();

    private static AdminDashboardController NewController(IAdminDashboardService svc) =>
        new(svc);

    [Fact]
    public void Controller_HasCnasAdminAuthorizationPolicy()
    {
        var attrs = typeof(AdminDashboardController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();

        attrs.Should().NotBeEmpty(
            "the admin dashboard controller MUST be gated by an explicit [Authorize] attribute.");
        attrs.Should().Contain(a => a.Policy == AuthorizationComposition.CnasAdmin,
            "the policy must be CnasAdmin so only cnas-admin users reach this surface.");
    }

    [Fact]
    public async Task GetAsync_Success_Returns200WithDto()
    {
        var svc = NewSvc();
        var dto = new AdminDashboardDto(
            Kpis: new Dictionary<string, decimal> { ["Applications.Pending"] = 5m },
            RecentAlerts: [],
            AuditSummary: [],
            OpenAdminActionsCount: 0,
            PerfMetrics: [],
            SnapshotAtUtc: new DateTime(2026, 5, 22, 9, 0, 0, DateTimeKind.Utc),
            Warning: null);
        svc.GetSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(Result<AdminDashboardDto>.Success(dto));
        var controller = NewController(svc);

        var result = await controller.GetAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task GetAsync_Failure_ReturnsProblem()
    {
        var svc = NewSvc();
        svc.GetSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(Result<AdminDashboardDto>.Failure(ErrorCodes.Internal, "boom"));
        var controller = NewController(svc);

        var result = await controller.GetAsync(CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}
