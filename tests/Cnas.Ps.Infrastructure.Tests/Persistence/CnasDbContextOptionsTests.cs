using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

namespace Cnas.Ps.Infrastructure.Tests.Persistence;

/// <summary>
/// R2175 / R2134 — sizing + connection-string contract for
/// <see cref="CnasDbContextOptions"/>. The options type centralises the
/// primary / replica connection strings consumed by the
/// <see cref="DataPersistenceServiceCollectionExtensions.AddCnasDataPersistence"/>
/// DI extension so the OLTP / OLAP split (TOR ARH 025) and the read-replica
/// reporting routing (TOR PSR 006) can be configured from one place.
/// </summary>
/// <remarks>
/// The tests deliberately exercise both the strongly-typed default constructor
/// and the configuration-binder pipeline; the Helm chart writes
/// <c>Cnas__Database__PrimaryConnectionString</c> as a pod env var so the
/// dotted-key path must round-trip.
/// </remarks>
public sealed class CnasDbContextOptionsTests
{
    /// <summary>
    /// Defaults: no primary, no replica, command timeout 30s (matches
    /// <see cref="PostgresPoolOptions.CommandTimeout"/>). The PrimaryConnectionString
    /// is REQUIRED by the DI extension — a constructor that defaulted it to a
    /// non-empty literal would hide misconfiguration.
    /// </summary>
    [Fact]
    public void Defaults_PrimaryAndReplicaUnset_TimeoutMatchesPool()
    {
        var opts = new CnasDbContextOptions();

        opts.PrimaryConnectionString.Should().BeEmpty(
            "the primary connection string is operator-supplied; no embedded default.");
        opts.ReplicaConnectionString.Should().BeNull(
            "an unset replica connection string signals fallback to the primary.");
        opts.ReplicaCommandTimeoutSeconds.Should().Be(30,
            "matches PostgresPoolOptions.CommandTimeout — reporting queries share the same per-call budget by default.");
    }

    /// <summary>
    /// SectionName is the stable configuration key consumed by the DI helper —
    /// changing it is a breaking change for every deployment chart.
    /// </summary>
    [Fact]
    public void SectionName_IsStable()
    {
        CnasDbContextOptions.SectionName.Should().Be("Cnas:Database");
    }

    /// <summary>
    /// Binder smoke test — operators populate
    /// <c>Cnas:Database:PrimaryConnectionString</c>,
    /// <c>Cnas:Database:ReplicaConnectionString</c>, and
    /// <c>Cnas:Database:ReplicaCommandTimeoutSeconds</c>; the standard
    /// <see cref="ConfigurationBinder"/> pipeline must populate the snapshot.
    /// </summary>
    [Fact]
    public void Binder_PopulatesAllThreeValues()
    {
        var inMemory = new Dictionary<string, string?>
        {
            ["Cnas:Database:PrimaryConnectionString"] = "Host=primary;Database=cnas",
            ["Cnas:Database:ReplicaConnectionString"] = "Host=replica;Database=cnas",
            ["Cnas:Database:ReplicaCommandTimeoutSeconds"] = "120",
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemory)
            .Build();

        var bound = new CnasDbContextOptions();
        config.GetSection(CnasDbContextOptions.SectionName).Bind(bound);

        bound.PrimaryConnectionString.Should().Be("Host=primary;Database=cnas");
        bound.ReplicaConnectionString.Should().Be("Host=replica;Database=cnas");
        bound.ReplicaCommandTimeoutSeconds.Should().Be(120);
    }

    /// <summary>
    /// When only the primary is configured, the binder leaves the replica null;
    /// downstream wiring will fall back to the primary with a WARN log. This is
    /// the dev / single-Postgres staging topology.
    /// </summary>
    [Fact]
    public void Binder_PrimaryOnly_LeavesReplicaNull()
    {
        var inMemory = new Dictionary<string, string?>
        {
            ["Cnas:Database:PrimaryConnectionString"] = "Host=primary",
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemory)
            .Build();

        var bound = new CnasDbContextOptions();
        config.GetSection(CnasDbContextOptions.SectionName).Bind(bound);

        bound.PrimaryConnectionString.Should().Be("Host=primary");
        bound.ReplicaConnectionString.Should().BeNull();
        bound.ReplicaCommandTimeoutSeconds.Should().Be(30, "the default survives a partial bind.");
    }
}
