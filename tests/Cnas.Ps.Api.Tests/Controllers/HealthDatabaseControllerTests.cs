using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Api.Health;
using Cnas.Ps.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R2175 / R2134 — tests for <see cref="HealthDatabaseController"/>. The
/// controller surfaces the primary / replica readiness states via a single
/// <c>GET /api/health/database</c> endpoint so dashboards can render an
/// OLTP / OLAP split health indicator. The status payload is the JSON
/// rendering of <see cref="DatabaseHealthStatusDto"/>.
/// </summary>
public sealed class HealthDatabaseControllerTests
{
    /// <summary>
    /// Both endpoints reachable → 200 with both states populated as
    /// <c>Healthy</c>. This is the production-steady-state response.
    /// </summary>
    [Fact]
    public async Task Get_BothHealthy_Returns200WithBothStates()
    {
        var probe = Substitute.For<IDatabaseConnectionProbe>();
        probe.IsReachableAsync("primary", Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        probe.IsReachableAsync("replica", Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var controller = BuildController(probe, primary: "primary", replica: "replica");

        var result = await controller.GetAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<DatabaseHealthStatusDto>().Subject;
        dto.Primary.Should().Be("Healthy");
        dto.Replica.Should().Be("Healthy");
    }

    /// <summary>
    /// Primary up, replica down → 200 with replica reported as Degraded. The
    /// controller does NOT surface 503 because the system is still serving
    /// reads (callers may transparently fall back to the primary). A 200 with
    /// a degraded body is the correct signal for partial degradation.
    /// </summary>
    [Fact]
    public async Task Get_ReplicaDown_Returns200WithDegradedReplica()
    {
        var probe = Substitute.For<IDatabaseConnectionProbe>();
        probe.IsReachableAsync("primary", Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        probe.IsReachableAsync("replica", Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        var controller = BuildController(probe, primary: "primary", replica: "replica");

        var result = await controller.GetAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<DatabaseHealthStatusDto>().Subject;
        dto.Primary.Should().Be("Healthy");
        dto.Replica.Should().Be("Degraded");
    }

    /// <summary>
    /// Primary unreachable → 503. Returning 200 here would lie to the load
    /// balancer; the pod must drain. The body still carries the per-endpoint
    /// states so dashboards can render the JSON.
    /// </summary>
    [Fact]
    public async Task Get_PrimaryDown_Returns503()
    {
        var probe = Substitute.For<IDatabaseConnectionProbe>();
        probe.IsReachableAsync("primary", Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        probe.IsReachableAsync("replica", Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var controller = BuildController(probe, primary: "primary", replica: "replica");

        var result = await controller.GetAsync(CancellationToken.None);

        var status = result.Result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        var dto = status.Value.Should().BeOfType<DatabaseHealthStatusDto>().Subject;
        dto.Primary.Should().Be("Unhealthy");
        dto.Replica.Should().Be("Healthy");
    }

    /// <summary>
    /// Single-Postgres staging (replica connection string = primary) returns
    /// 200 and reports both as Healthy when reachable. The probe is invoked
    /// once because the resolver collapses the identical strings.
    /// </summary>
    [Fact]
    public async Task Get_PrimaryOnlyTopology_Returns200()
    {
        var probe = Substitute.For<IDatabaseConnectionProbe>();
        probe.IsReachableAsync("primary", Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var controller = BuildController(probe, primary: "primary", replica: "primary");

        var result = await controller.GetAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<DatabaseHealthStatusDto>().Subject;
        dto.Primary.Should().Be("Healthy");
        dto.Replica.Should().Be("Healthy");
    }

    /// <summary>
    /// Constructs the controller with a stub probe. The
    /// <see cref="DatabaseReplicaHealthCheck"/> dependency is wired directly
    /// (no DI container needed) so the controller test stays an isolated
    /// unit test.
    /// </summary>
    private static HealthDatabaseController BuildController(IDatabaseConnectionProbe probe, string primary, string replica)
    {
        var check = new DatabaseReplicaHealthCheck(probe, primary, replica);
        return new HealthDatabaseController(check)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
    }
}
