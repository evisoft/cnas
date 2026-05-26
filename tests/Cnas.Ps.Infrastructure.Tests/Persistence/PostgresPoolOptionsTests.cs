using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

namespace Cnas.Ps.Infrastructure.Tests.Persistence;

/// <summary>
/// R0025 — sizing contract for <see cref="PostgresPoolOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// The CNAS „Protecția Socială" SLO (TOR PSR 003) is 2000 concurrent users.
/// Each in-flight web request can hold a database connection for its duration,
/// so at peak we expect ~2000 active connections per pod. PgBouncer in
/// transaction-pooling mode then multiplexes those onto ~50 actual Postgres
/// backends (see <c>docs/operations.md</c> §"Database connection pooling").
/// </para>
/// <para>
/// These tests pin two contracts:
/// <list type="number">
///   <item>The default <see cref="PostgresPoolOptions.MaxPoolSize"/> equals the TOR
///         PSR 003 target (2000). Any change to the default without a corresponding
///         TOR update will fail this test.</item>
///   <item>The configuration binder populates the section correctly so operators
///         can override the per-pod cap via the <c>Postgres:Pool:MaxPoolSize</c>
///         environment variable (used by Helm chart and the Kubernetes deployment).</item>
/// </list>
/// </para>
/// <para>
/// We do NOT try to open a live Npgsql connection — there is no Postgres available
/// for unit tests. The DbContext composition with <c>UseNpgsql(...)</c> is verified
/// by the existing build + the e2e fixture using the InMemory provider.
/// </para>
/// </remarks>
public class PostgresPoolOptionsTests
{
    /// <summary>
    /// Locks the TOR PSR 003 target into the default value of
    /// <see cref="PostgresPoolOptions.MaxPoolSize"/>. If the SLO changes, update
    /// both the requirement document AND this test in the same commit.
    /// </summary>
    [Fact]
    public void Defaults_MatchPsr003_2000Concurrent()
    {
        var defaults = new PostgresPoolOptions();

        defaults.MaxPoolSize.Should().Be(2000,
            "TOR PSR 003 SLO is 2000 concurrent users; the per-pod Npgsql pool MUST be sized accordingly.");
        defaults.MinPoolSize.Should().Be(5);
        defaults.ConnectionIdleLifetime.Should().Be(300);
        defaults.ConnectionPruningInterval.Should().Be(10);
        defaults.CommandTimeout.Should().Be(30);
        defaults.UsePgBouncer.Should().BeTrue(
            "production fronts Postgres with PgBouncer in transaction-pooling mode (R0025); " +
            "the defaults must reflect the production topology so a misconfigured pod still " +
            "speaks PgBouncer-compatible SQL out of the box.");
    }

    /// <summary>
    /// Smoke test that the standard <see cref="ConfigurationBinder"/> pipeline binds the
    /// <see cref="PostgresPoolOptions.SectionName"/> section onto the options instance.
    /// The Helm chart writes <c>Postgres__Pool__MaxPoolSize</c> as a pod env var; this
    /// test exercises the equivalent dotted-key path that <c>AddOptions().Bind(section)</c>
    /// follows.
    /// </summary>
    [Fact]
    public void Binder_ConfiguresConnectionString_AsExpected()
    {
        var inMemory = new Dictionary<string, string?>
        {
            ["Postgres:Pool:MaxPoolSize"] = "1500",
            ["Postgres:Pool:MinPoolSize"] = "7",
            ["Postgres:Pool:CommandTimeout"] = "45",
            ["Postgres:Pool:UsePgBouncer"] = "false",
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemory)
            .Build();

        var bound = new PostgresPoolOptions();
        config.GetSection(PostgresPoolOptions.SectionName).Bind(bound);

        bound.MaxPoolSize.Should().Be(1500);
        bound.MinPoolSize.Should().Be(7);
        bound.CommandTimeout.Should().Be(45);
        bound.UsePgBouncer.Should().BeFalse();

        // Unspecified keys keep their defaults — operators should be able to override
        // a single value without restating the rest.
        bound.ConnectionIdleLifetime.Should().Be(300);
        bound.ConnectionPruningInterval.Should().Be(10);
    }
}
