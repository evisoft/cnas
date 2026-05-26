using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Kpi;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0201 / TOR CF 20.02 — tests for <see cref="KpiDashboardController"/>.
/// Direct-construction style mirroring the rest of the controller suite; the
/// <see cref="IKpiSnapshotService"/> dependency is faked with NSubstitute.
/// </summary>
public sealed class KpiDashboardControllerTests
{
    private static KpiDashboardController NewController(IKpiSnapshotService svc) => new(svc);

    [Fact]
    public void Controller_HasAuthorizationPolicy()
    {
        var attrs = typeof(KpiDashboardController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();

        attrs.Should().NotBeEmpty("the controller MUST be gated by an explicit Authorize policy");
    }

    [Fact]
    public async Task RunAsync_HappyPath_Returns200WithRunDto()
    {
        var svc = Substitute.For<IKpiSnapshotService>();
        var dto = new KpiSnapshotRunDto("abc123", new(2026, 5, 21), 5, 10, 42);
        svc.RunForDateAsync(new DateOnly(2026, 5, 21), Arg.Any<CancellationToken>())
            .Returns(Result<KpiSnapshotRunDto>.Success(dto));
        var controller = NewController(svc);

        var result = await controller.RunAsync(new DateOnly(2026, 5, 21), CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
        // Sqid round-trip: the run dto's Id is a non-empty opaque string.
        dto.Id.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RunAsync_FailureFromService_ReturnsProblem()
    {
        var svc = Substitute.For<IKpiSnapshotService>();
        svc.RunForDateAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Result<KpiSnapshotRunDto>.Failure(ErrorCodes.Internal, "boom"));
        var controller = NewController(svc);

        var result = await controller.RunAsync(new DateOnly(2026, 5, 21), CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetLatestAsync_ReturnsDictionary()
    {
        var svc = Substitute.For<IKpiSnapshotService>();
        var data = new Dictionary<string, decimal>
        {
            ["Applications.Pending"] = 12m,
            ["Tasks.Overdue"] = 3m,
        };
        svc.GetLatestAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(data);
        var controller = NewController(svc);

        var result = await controller.GetLatestAsync(
            "Applications.Pending,Tasks.Overdue", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeAssignableTo<IReadOnlyDictionary<string, decimal>>()
            .Which.Should().ContainKey("Applications.Pending");
    }

    [Fact]
    public async Task ListSnapshotsAsync_ReturnsDtoList()
    {
        var svc = Substitute.For<IKpiSnapshotService>();
        var rows = new List<KpiSnapshotDto>
        {
            new(new(2026, 5, 22), "Applications.Pending", 12m, "count", string.Empty, string.Empty),
        };
        svc.QueryAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(rows);
        var controller = NewController(svc);

        var result = await controller.ListSnapshotsAsync(
            fromDate: new(2026, 5, 20),
            toDate: new(2026, 5, 22),
            kpiCode: null,
            cancellationToken: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(rows);
    }

    /// <summary>Sanity check on AuthorizationComposition policy strings exist.</summary>
    [Fact]
    public void AuthorizationComposition_ExposesPolicyConstants()
    {
        AuthorizationComposition.CnasUser.Should().NotBeNullOrEmpty();
        AuthorizationComposition.CnasTechAdmin.Should().NotBeNullOrEmpty();
    }
}
