using System.Linq;
using System.Reflection;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Tests.Persistence;

/// <summary>
/// R0026 — contract tests for <see cref="CnasReadOnlyDbContext"/> and
/// <see cref="IReadOnlyCnasDbContext"/>. The read-only DbContext routes Annex 6
/// aggregations and Annex 5/6 long-running list queries to a Postgres
/// streaming-replication replica per TOR PSR 006 / ARH 025; these tests pin the
/// behavioural contract that lets reporting / listing services consume the seam
/// safely.
/// </summary>
/// <remarks>
/// <para>
/// The fixture deliberately uses the EF Core InMemory provider so the suite can
/// run without a live Postgres instance. Both <see cref="CnasDbContext"/> and
/// <see cref="CnasReadOnlyDbContext"/> are wired against the SAME InMemory
/// database name so the cross-context test (seed via the writer, read back via
/// the reader) round-trips deterministically — mirroring the topology of
/// production where the streaming replica eventually catches up with the primary.
/// </para>
/// </remarks>
public class CnasReadOnlyDbContextTests
{
    /// <summary>
    /// Locks the no-tracking default. Reporting and listing queries don't mutate
    /// rows — keeping tracking off is a small win on every aggregation and a
    /// belt-and-braces guard against accidental write-attempts via the read-only
    /// surface. Pinned in the constructor of <see cref="CnasReadOnlyDbContext"/>
    /// rather than left to the caller.
    /// </summary>
    [Fact]
    public void Constructor_TurnsOffChangeTracking()
    {
        var opts = new DbContextOptionsBuilder<CnasReadOnlyDbContext>()
            .UseInMemoryDatabase($"cnas-readonly-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        using var db = new CnasReadOnlyDbContext(opts);

        db.ChangeTracker.QueryTrackingBehavior.Should().Be(QueryTrackingBehavior.NoTracking,
            "the read-only context exists to serve aggregations and listing queries; tracking is dead weight.");
        db.ChangeTracker.AutoDetectChangesEnabled.Should().BeFalse(
            "AutoDetectChanges only matters before SaveChanges; the read-only context blocks SaveChanges anyway.");
    }

    /// <summary>
    /// Pins the read-only contract on <see cref="CnasReadOnlyDbContext.SaveChangesAsync(CancellationToken)"/> —
    /// callers that accidentally try to write through the replica surface must
    /// see a loud <see cref="InvalidOperationException"/>, not a silent commit
    /// against a possibly-stale snapshot.
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_Throws()
    {
        var opts = new DbContextOptionsBuilder<CnasReadOnlyDbContext>()
            .UseInMemoryDatabase($"cnas-readonly-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        using var db = new CnasReadOnlyDbContext(opts);

        var act = async () => await db.SaveChangesAsync();

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("read-only",
            "the diagnostic must name the context so a developer hitting this in production knows immediately which abstraction was the wrong one to use.");
    }

    /// <summary>
    /// End-to-end check that a row inserted via <see cref="ICnasDbContext"/> is
    /// visible through <see cref="IReadOnlyCnasDbContext"/> when both contexts
    /// share the same EF Core InMemory database name. This is the test fixture
    /// invariant that lets cross-context test flows work — production replicas
    /// have eventual-consistency lag, but the InMemory store is synchronous so
    /// the seed→read round-trip is deterministic.
    /// </summary>
    [Fact]
    public async Task Notifications_Query_ReturnsDataFromUnderlyingProvider()
    {
        var dbName = $"cnas-readonly-bridge-{Guid.NewGuid():N}";

        // Writer — primary connection in production, primary InMemory shard in tests.
        var writerOpts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using (var writer = new CnasDbContext(writerOpts))
        {
            writer.Notifications.Add(new Notification
            {
                CreatedAtUtc = DateTime.UtcNow,
                RecipientUserId = 1L,
                Channel = NotificationChannel.InApp,
                Subject = "Test",
                Body = "Body",
                DeliveryStatus = NotificationDeliveryStatus.Delivered,
                IsActive = true,
            });
            await writer.SaveChangesAsync();
        }

        // Reader — replica connection in production, same InMemory shard in tests.
        var readerOpts = new DbContextOptionsBuilder<CnasReadOnlyDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var reader = new CnasReadOnlyDbContext(readerOpts);
        IReadOnlyCnasDbContext readonlyView = reader;

        var rows = await readonlyView.Notifications.ToListAsync();

        rows.Should().HaveCount(1);
        rows[0].Subject.Should().Be("Test");
    }

    /// <summary>
    /// Reflection-based drift guard — every <see cref="DbSet{TEntity}"/> property
    /// on <see cref="ICnasDbContext"/> MUST have a corresponding
    /// <see cref="IQueryable{T}"/> property of the same entity type on
    /// <see cref="IReadOnlyCnasDbContext"/>. When a new entity ships, both
    /// interfaces grow in the same commit; this test fails loudly when one is
    /// added without the other.
    /// </summary>
    [Fact]
    public void EveryDbSet_IsMirroredAsIQueryable()
    {
        var dbSetProps = typeof(ICnasDbContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType
                && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .ToList();

        dbSetProps.Should().NotBeEmpty(
            "ICnasDbContext is the read/write seam — it must expose at least one DbSet.");

        var readonlyProps = typeof(IReadOnlyCnasDbContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name, p => p.PropertyType);

        foreach (var dbSetProp in dbSetProps)
        {
            // Every DbSet<T> must be mirrored as IQueryable<T> with the same property name.
            readonlyProps.Should().ContainKey(dbSetProp.Name,
                $"IReadOnlyCnasDbContext is missing a mirror for ICnasDbContext.{dbSetProp.Name} " +
                $"— new entities must be added to both interfaces in the same commit.");

            var entityType = dbSetProp.PropertyType.GetGenericArguments()[0];
            var expected = typeof(IQueryable<>).MakeGenericType(entityType);
            readonlyProps[dbSetProp.Name].Should().Be(expected,
                $"IReadOnlyCnasDbContext.{dbSetProp.Name} must be IQueryable<{entityType.Name}>, " +
                $"not the DbSet shape — the read-only surface must not expose Add / Remove / Update.");
        }
    }

    /// <summary>
    /// Operators MUST see a WARN-level log line when the replica connection
    /// string is unset and the implementation falls back to the primary. The
    /// fallback is acceptable for dev (single Postgres) but a misconfiguration
    /// in production (TOR PSR 006) — surfacing it loudly prevents silent loss
    /// of replica isolation.
    /// </summary>
    [Fact]
    public void FailoverWarning_LoggedAtStartup_WhenReplicaConnectionStringMissing()
    {
        var captured = new List<string>();
        var loggerFactory = new CapturingLoggerFactory(captured);

        // Inject a non-empty PRIMARY connection string but no PostgresReadReplica.
        // The wiring helper resolves the same primary connection string and emits
        // the WARN line via the supplied logger factory.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=cnas;Username=cnas;Password=cnas",
                // ConnectionStrings:PostgresReadReplica intentionally omitted.
            })
            .Build();

        var resolved = ReadReplicaConfiguration.ResolveConnectionString(config, loggerFactory);

        resolved.Should().Be("Host=localhost;Database=cnas;Username=cnas;Password=cnas");
        captured.Should().ContainSingle(s => s.Contains("PostgresReadReplica", StringComparison.Ordinal)
            && s.Contains("primary", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Captures every log message emitted through <see cref="ILogger.Log"/> so
    /// the test can assert on the WARN diagnostic without booting a real
    /// logging backend.
    /// </summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _sink;

        public CapturingLoggerFactory(List<string> sink) => _sink = sink;

        public void AddProvider(ILoggerProvider provider)
        {
            // Tests don't need provider chaining — sink-only.
        }

        public ILogger CreateLogger(string categoryName) => new SinkLogger(_sink);

        public void Dispose()
        {
            // Nothing to release — the sink belongs to the test, not the factory.
        }

        private sealed class SinkLogger : ILogger
        {
            private readonly List<string> _sink;

            public SinkLogger(List<string> sink) => _sink = sink;

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _sink.Add(formatter(state, exception));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose()
                {
                    // No-op.
                }
            }
        }
    }
}
