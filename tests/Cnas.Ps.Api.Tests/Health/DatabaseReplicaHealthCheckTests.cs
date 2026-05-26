using Cnas.Ps.Api.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Health;

/// <summary>
/// R2175 / R2134 — readiness contract for <see cref="DatabaseReplicaHealthCheck"/>.
/// The probe issues a cheap <c>SELECT 1</c> against the primary AND replica
/// connection strings and classifies the result triple:
/// <list type="bullet">
///   <item><see cref="HealthStatus.Healthy"/> — both endpoints reachable.</item>
///   <item><see cref="HealthStatus.Degraded"/> — primary reachable, replica
///         unreachable. Reporting can transparently fall back to the primary;
///         the alert is not pager-worthy but operators must see it.</item>
///   <item><see cref="HealthStatus.Unhealthy"/> — primary unreachable. The
///         backend is offline and the load balancer must drain the pod.</item>
/// </list>
/// The check is injected with an <see cref="IDatabaseConnectionProbe"/> so
/// tests don't need to spin up a real Postgres backend.
/// </summary>
public sealed class DatabaseReplicaHealthCheckTests
{
    /// <summary>
    /// Both probes succeed → <see cref="HealthStatus.Healthy"/>. The result
    /// data dictionary carries the per-endpoint status so the controller
    /// surface (<c>/api/health/database</c>) can render the JSON payload
    /// without re-probing.
    /// </summary>
    [Fact]
    public async Task BothEndpointsHealthy_ReturnsHealthy()
    {
        var probe = Substitute.For<IDatabaseConnectionProbe>();
        probe.IsReachableAsync("primary-cs", Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        probe.IsReachableAsync("replica-cs", Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var sut = new DatabaseReplicaHealthCheck(probe, "primary-cs", "replica-cs");

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["primary"].Should().Be("Healthy");
        result.Data["replica"].Should().Be("Healthy");
    }

    /// <summary>
    /// Primary reachable, replica unreachable → <see cref="HealthStatus.Degraded"/>.
    /// Reporting can transparently route to the primary (the same fallback the
    /// DI wiring uses when no replica is configured); operators see a
    /// non-pager-worthy yellow indicator.
    /// </summary>
    [Fact]
    public async Task PrimaryHealthyReplicaDown_ReturnsDegraded()
    {
        var probe = Substitute.For<IDatabaseConnectionProbe>();
        probe.IsReachableAsync("primary-cs", Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        probe.IsReachableAsync("replica-cs", Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        var sut = new DatabaseReplicaHealthCheck(probe, "primary-cs", "replica-cs");

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["primary"].Should().Be("Healthy");
        result.Data["replica"].Should().Be("Degraded");
    }

    /// <summary>
    /// Primary unreachable → <see cref="HealthStatus.Unhealthy"/>. The replica
    /// status is reported regardless so dashboards stay rich, but the overall
    /// classification escalates because the backend is offline.
    /// </summary>
    [Fact]
    public async Task PrimaryDown_ReturnsUnhealthy()
    {
        var probe = Substitute.For<IDatabaseConnectionProbe>();
        probe.IsReachableAsync("primary-cs", Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        probe.IsReachableAsync("replica-cs", Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var sut = new DatabaseReplicaHealthCheck(probe, "primary-cs", "replica-cs");

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data["primary"].Should().Be("Unhealthy");
        result.Data["replica"].Should().Be("Healthy");
    }

    /// <summary>
    /// Both endpoints unreachable → <see cref="HealthStatus.Unhealthy"/>. The
    /// classification follows the primary because primary-down is the worst
    /// case; the replica is reported as Degraded so the JSON payload stays
    /// readable.
    /// </summary>
    [Fact]
    public async Task BothDown_ReturnsUnhealthy()
    {
        var probe = Substitute.For<IDatabaseConnectionProbe>();
        probe.IsReachableAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        var sut = new DatabaseReplicaHealthCheck(probe, "primary-cs", "replica-cs");

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data["primary"].Should().Be("Unhealthy");
        result.Data["replica"].Should().Be("Degraded");
    }

    /// <summary>
    /// When the replica connection string is null OR identical to the primary,
    /// the probe runs once and the replica state mirrors the primary —
    /// dev / single-Postgres staging deployments should not double-charge the
    /// readiness budget for the same backend.
    /// </summary>
    [Fact]
    public async Task ReplicaConnectionStringMatchesPrimary_ProbesOnce()
    {
        var probe = Substitute.For<IDatabaseConnectionProbe>();
        probe.IsReachableAsync("only-cs", Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var sut = new DatabaseReplicaHealthCheck(probe, "only-cs", replicaConnectionString: "only-cs");

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["primary"].Should().Be("Healthy");
        result.Data["replica"].Should().Be("Healthy");
        await probe.Received(1).IsReachableAsync("only-cs", Arg.Any<CancellationToken>());
    }
}
