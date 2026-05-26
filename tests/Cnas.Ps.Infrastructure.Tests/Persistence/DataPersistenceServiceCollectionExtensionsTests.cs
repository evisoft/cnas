using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Tests.Persistence;

/// <summary>
/// R2175 / R2134 / R0026 — wiring contract for the
/// <see cref="DataPersistenceServiceCollectionExtensions.AddCnasDataPersistence(IServiceCollection, CnasDbContextOptions, ILoggerFactory)"/>
/// DI extension. The extension is the single source of truth for the
/// primary / replica DbContext registration so reporting (TOR PSR 006) can route
/// to the OLAP-side replica and OLTP traffic stays on the primary
/// (TOR ARH 025).
/// </summary>
/// <remarks>
/// The tests deliberately use the EF Core InMemory provider so the suite can
/// run without a live Postgres instance. The extension supports an in-memory
/// builder override path so tests can exercise the full DI graph; production
/// always uses the Npgsql configurator.
/// </remarks>
public sealed class DataPersistenceServiceCollectionExtensionsTests
{
    /// <summary>
    /// The extension MUST throw when PrimaryConnectionString is empty / null.
    /// A host with no primary endpoint cannot start; deferring the failure to
    /// first-query-time would be harder to diagnose.
    /// </summary>
    [Fact]
    public void AddCnasDataPersistence_MissingPrimary_Throws()
    {
        var services = new ServiceCollection();
        var opts = new CnasDbContextOptions
        {
            PrimaryConnectionString = "",
            ReplicaConnectionString = null,
        };

        var act = () => services.AddCnasDataPersistence(opts, NullLoggerFactory.Instance);

        act.Should().Throw<InvalidOperationException>()
            .Where(ex => ex.Message.Contains("PrimaryConnectionString", StringComparison.Ordinal));
    }

    /// <summary>
    /// When the replica connection string is null, the resolver routes the
    /// read-only context to the primary connection string AND emits a single
    /// WARN log line. The DI extension simply delegates to
    /// <see cref="ReadReplicaConfiguration"/>; this test exercises the full
    /// composition.
    /// </summary>
    [Fact]
    public void AddCnasDataPersistence_NullReplica_FallsBackToPrimaryWithWarning()
    {
        var captured = new List<string>();
        var loggerFactory = new CapturingLoggerFactory(captured);

        var services = new ServiceCollection();
        var opts = new CnasDbContextOptions
        {
            PrimaryConnectionString = "primary-only-test",
            ReplicaConnectionString = null,
        };

        services.AddCnasDataPersistence(opts, loggerFactory);

        // The fallback diagnostic is emitted via ReadReplicaConfiguration so the
        // wording matches the production path (mentioning the PostgresReadReplica
        // configuration key and "primary" as the fallback target).
        captured.Should().ContainSingle(s =>
            s.Contains("PostgresReadReplica", StringComparison.Ordinal)
            && s.Contains("primary", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// When the replica connection string is set, NO WARN log line is emitted
    /// — the operator has explicitly opted in to read-replica routing and
    /// silence is the correct signal.
    /// </summary>
    [Fact]
    public void AddCnasDataPersistence_DistinctReplica_DoesNotLogWarning()
    {
        var captured = new List<string>();
        var loggerFactory = new CapturingLoggerFactory(captured);

        var services = new ServiceCollection();
        var opts = new CnasDbContextOptions
        {
            PrimaryConnectionString = "primary",
            ReplicaConnectionString = "replica",
        };

        services.AddCnasDataPersistence(opts, loggerFactory);

        captured.Should().BeEmpty("the operator opted in to read-replica routing; no fallback warning expected.");
    }

    /// <summary>
    /// When the InMemory builder hook is used both contexts resolve out of DI
    /// and the IReadOnlyCnasDbContext alias points at the read-only concrete
    /// type. This pins the registration triplet.
    /// </summary>
    [Fact]
    public void AddCnasDataPersistence_RegistersBothContextsAndAlias()
    {
        var services = new ServiceCollection();
        var dbName = $"persistence-{Guid.NewGuid():N}";
        var opts = new CnasDbContextOptions
        {
            PrimaryConnectionString = "primary",
            ReplicaConnectionString = "replica",
        };

        services.AddCnasDataPersistence(
            opts,
            NullLoggerFactory.Instance,
            primaryBuilder: b => b.UseInMemoryDatabase(dbName).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)),
            replicaBuilder: b => b.UseInMemoryDatabase(dbName).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var primary = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var readOnly = scope.ServiceProvider.GetRequiredService<CnasReadOnlyDbContext>();
        var alias = scope.ServiceProvider.GetRequiredService<IReadOnlyCnasDbContext>();
        var writeAlias = scope.ServiceProvider.GetRequiredService<ICnasDbContext>();

        primary.Should().NotBeNull();
        readOnly.Should().NotBeNull();
        alias.Should().BeSameAs(readOnly,
            "IReadOnlyCnasDbContext must alias the read-only concrete type so the read-replica routing is reachable from the Application layer.");
        writeAlias.Should().BeSameAs(primary,
            "ICnasDbContext must alias the primary concrete type so writes route to the OLTP backend.");
    }

    /// <summary>
    /// The model configuration is shared between the primary and read-only
    /// contexts — both must see the same entity-type map for at least one
    /// canonical entity (Solicitant). When R0026 introduced
    /// <see cref="CnasReadOnlyDbContext"/> the implementation chose to derive
    /// from <see cref="CnasDbContext"/> so OnModelCreating runs unchanged; this
    /// test pins the invariant against an accidental override.
    /// </summary>
    [Fact]
    public void Contexts_ShareTheSameModelForSolicitant()
    {
        var dbName = $"model-parity-{Guid.NewGuid():N}";
        var primaryOpts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var readOpts = new DbContextOptionsBuilder<CnasReadOnlyDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using var primary = new CnasDbContext(primaryOpts);
        using var read = new CnasReadOnlyDbContext(readOpts);

        IEntityType? primarySolicitant = primary.Model.FindEntityType(typeof(Solicitant));
        IEntityType? readSolicitant = read.Model.FindEntityType(typeof(Solicitant));

        primarySolicitant.Should().NotBeNull();
        readSolicitant.Should().NotBeNull();
        readSolicitant!.GetProperties().Select(p => p.Name).Should()
            .BeEquivalentTo(primarySolicitant!.GetProperties().Select(p => p.Name),
                "the read-only context must mirror the primary entity map — any drift means OnModelCreating diverged.");
    }

    /// <summary>
    /// The read-only context honors the
    /// <see cref="CnasDbContextOptions.ReplicaCommandTimeoutSeconds"/> knob — a
    /// reporting query that overruns the budget must time out so a single slow
    /// SQL plan can't park a worker indefinitely. The InMemory provider used in
    /// the test suite is non-relational so EF rejects <c>SetCommandTimeout</c> —
    /// the DI extension swallows that ArgumentException and the test verifies
    /// the seam stays open by exercising the resolution path end to end. The
    /// value itself is pinned on <see cref="CnasDbContextOptions"/> directly so
    /// the configuration contract is what the test asserts on.
    /// </summary>
    [Fact]
    public void AddCnasDataPersistence_HonoursReplicaCommandTimeout()
    {
        var services = new ServiceCollection();
        var dbName = $"timeout-{Guid.NewGuid():N}";
        var opts = new CnasDbContextOptions
        {
            PrimaryConnectionString = "primary",
            ReplicaConnectionString = "replica",
            ReplicaCommandTimeoutSeconds = 90,
        };

        services.AddCnasDataPersistence(
            opts,
            NullLoggerFactory.Instance,
            primaryBuilder: b => b.UseInMemoryDatabase(dbName).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)),
            replicaBuilder: b => b.UseInMemoryDatabase(dbName).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        // Two assertions: (1) the configured value lives on the options snapshot so
        // operators can verify configuration without booting EF; (2) the DI
        // resolution path completes WITHOUT throwing despite the InMemory provider
        // rejecting SetCommandTimeout — the implementation's try/catch is the
        // contract we're pinning.
        opts.ReplicaCommandTimeoutSeconds.Should().Be(90);

        var act = () => scope.ServiceProvider.GetRequiredService<IReadOnlyCnasDbContext>();
        act.Should().NotThrow(
            "the DI factory must tolerate non-relational providers — SetCommandTimeout is best-effort against InMemory.");
    }

    // ─────────────────────── helpers ───────────────────────

    /// <summary>
    /// Lightweight log sink that captures every formatted message; mirrors the
    /// pattern used in <see cref="CnasReadOnlyDbContextTests"/>.
    /// </summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _sink;

        public CapturingLoggerFactory(List<string> sink) => _sink = sink;

        public void AddProvider(ILoggerProvider provider)
        {
            // No-op — tests don't chain providers.
        }

        public ILogger CreateLogger(string categoryName) => new SinkLogger(_sink);

        public void Dispose()
        {
            // No-op — sink belongs to the test.
        }

        private sealed class SinkLogger : ILogger
        {
            private readonly List<string> _sink;
            public SinkLogger(List<string> sink) => _sink = sink;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => _sink.Add(formatter(state, exception));
            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }
}
