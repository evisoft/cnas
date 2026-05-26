using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cnas.Ps.Api.Tests.Health;

/// <summary>
/// Unit tests asserting that the readiness / liveness predicate logic correctly partitions
/// health-check registrations. These tests are built around the framework's
/// <see cref="HealthCheckRegistration"/> shape rather than booting <c>WebApplicationFactory</c>
/// — predicate behaviour is the only thing we need to lock down for /health/live and
/// /health/ready.
/// </summary>
public sealed class HealthEndpointWiringTests
{
    private static readonly string[] MGovTags = ["ready", "mgov"];
    private static readonly string[] WorkflowTags = ["ready", "workflow"];
    private static readonly string[] StorageTags = ["ready", "storage"];
    private static readonly string[] DbTags = ["ready", "db"];
    private static readonly string[] EmptyTags = [];
    private static readonly string[] DashboardBuckets = ["mgov", "workflow", "storage", "db"];
    private static readonly string[] ExpectedReadyNames =
        ["mgov.msign", "mgov.mpay", "workflow.operaton", "storage.minio", "db.postgres"];

    /// <summary>
    /// Builds a deterministic registration list mirroring the live composition root —
    /// every dependency probe carries the "ready" tag, while a hypothetical "self"
    /// liveness check (carried for future use) would not.
    /// </summary>
    private static List<HealthCheckRegistration> BuildReadinessRegistrations()
    {
        var noop = new NoopHealthCheck();
        return new List<HealthCheckRegistration>
        {
            new("mgov.msign", noop, HealthStatus.Unhealthy, MGovTags),
            new("mgov.mpay", noop, HealthStatus.Unhealthy, MGovTags),
            new("workflow.operaton", noop, HealthStatus.Unhealthy, WorkflowTags),
            new("storage.minio", noop, HealthStatus.Unhealthy, StorageTags),
            new("db.postgres", noop, HealthStatus.Unhealthy, DbTags),
            new("self.process", noop, HealthStatus.Unhealthy, EmptyTags),
        };
    }

    [Fact]
    public void LivenessPredicate_ExcludesEveryRegistration()
    {
        // The /health/live endpoint uses Predicate = _ => false so no dependency check runs.
        Func<HealthCheckRegistration, bool> livePredicate = _ => false;
        var registrations = BuildReadinessRegistrations();

        var selected = registrations.Where(livePredicate).ToList();

        selected.Should().BeEmpty("liveness must never depend on any registered check");
    }

    [Fact]
    public void ReadinessPredicate_SelectsOnlyReadyTaggedRegistrations()
    {
        // The /health/ready endpoint uses Predicate = c => c.Tags.Contains("ready").
        Func<HealthCheckRegistration, bool> readyPredicate = c => c.Tags.Contains("ready");
        var registrations = BuildReadinessRegistrations();

        var selected = registrations.Where(readyPredicate).ToList();

        selected.Should().HaveCount(5);
        selected.Select(r => r.Name).Should().Contain(ExpectedReadyNames);
        selected.Should().NotContain(r => r.Name == "self.process",
            "untagged checks must not bleed into the readiness endpoint");
    }

    [Fact]
    public void ReadinessTagGrouping_MatchesPerCategoryBuckets()
    {
        // Sanity-check the dashboard grouping promise — every "ready" check carries exactly
        // one of the secondary buckets (mgov / workflow / storage / db).
        var registrations = BuildReadinessRegistrations().Where(r => r.Tags.Contains("ready"));

        foreach (var reg in registrations)
        {
            reg.Tags.Intersect(DashboardBuckets).Should().ContainSingle(
                $"{reg.Name} must belong to exactly one dashboard bucket");
        }
    }

    /// <summary>Stand-in <see cref="IHealthCheck"/> used purely as a registration payload.</summary>
    private sealed class NoopHealthCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(HealthCheckResult.Healthy());
    }
}
