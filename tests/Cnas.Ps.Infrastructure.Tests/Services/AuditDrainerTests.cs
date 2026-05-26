using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Tests for <see cref="AuditDrainer"/> — the hosted service that drains the
/// in-memory <see cref="AuditWriteQueue"/> in batches and writes to the local
/// AuditLog table + mirrors to MLog. Drives the drainer via the internal
/// <c>FlushOnceAsync</c> test seam to keep the loop deterministic.
/// </summary>
/// <remarks>
/// Members of <see cref="CnasMeterCollection"/> — the drainer emits on the static
/// <see cref="Cnas.Ps.Infrastructure.Observability.CnasMeter"/> so cross-test
/// parallelism would pollute the meter listeners used by <c>CnasMeterTests</c>.
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public class AuditDrainerTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Drainer_FlushesAfterFiftyRecords()
    {
        // Arrange
        var queue = new AuditWriteQueue();
        using var harness = new DrainerHarness(queue);

        for (var i = 0; i < 50; i++)
        {
            queue.TryEnqueue(NewRecord(i.ToString())).Should().BeTrue();
        }

        // Act — drive one flush cycle.
        await harness.Drainer.FlushOnceAsync(CancellationToken.None);

        // Assert
        var db = harness.NewDb();
        (await db.AuditLogs.CountAsync()).Should().Be(50);
        await harness.Mlog.Received(50).AppendAsync(Arg.Any<MLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Drainer_FlushesAfterOneSecond_EvenWithFewerThanFiftyRecords()
    {
        // Arrange — only 3 records, well below the 50-batch threshold; the drainer's
        // wait-window must still flush them.
        var queue = new AuditWriteQueue();
        using var harness = new DrainerHarness(queue);

        queue.TryEnqueue(NewRecord("a")).Should().BeTrue();
        queue.TryEnqueue(NewRecord("b")).Should().BeTrue();
        queue.TryEnqueue(NewRecord("c")).Should().BeTrue();

        // Act — one cycle; FlushInterval must elapse without filling the buffer.
        await harness.Drainer.FlushOnceAsync(CancellationToken.None);

        // Assert
        var db = harness.NewDb();
        (await db.AuditLogs.CountAsync()).Should().Be(3);
        await harness.Mlog.Received(3).AppendAsync(Arg.Any<MLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Drainer_MlogForwardOrder_PreservedAcrossBatch()
    {
        // Arrange
        var queue = new AuditWriteQueue();
        using var harness = new DrainerHarness(queue);

        var captured = new List<string>();
        harness.Mlog
            .AppendAsync(Arg.Any<MLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var entry = call.Arg<MLogEntry>();
                captured.Add(entry.EventCode);
                return Task.FromResult(Result.Success());
            });

        for (var i = 0; i < 10; i++)
        {
            queue.TryEnqueue(NewRecord($"corr-{i:D2}", eventCode: $"EVT.{i:D2}")).Should().BeTrue();
        }

        // Act
        await harness.Drainer.FlushOnceAsync(CancellationToken.None);

        // Assert — capture order matches enqueue order.
        captured.Should().Equal(Enumerable.Range(0, 10).Select(i => $"EVT.{i:D2}"));
    }

    [Fact]
    public async Task Drainer_DbFailureDuringFlush_DropsBatch_AndContinues()
    {
        // Arrange — a poison harness whose first flush throws on SaveChangesAsync.
        var queue = new AuditWriteQueue();
        using var harness = new PoisonDrainerHarness(queue);

        queue.TryEnqueue(NewRecord("p1")).Should().BeTrue();

        // Act — first flush blows up; the drainer must NOT propagate, the batch is
        // routed to the archive (R0188) rather than dropped on the floor.
        await harness.Drainer.FlushOnceAsync(CancellationToken.None);

        // Now flip the poison off and enqueue another batch.
        harness.PoisonNextFlush = false;
        queue.TryEnqueue(NewRecord("p2")).Should().BeTrue();

        await harness.Drainer.FlushOnceAsync(CancellationToken.None);

        // Assert — only the recovered record persisted; the poisoned one is in the archive.
        var db = harness.NewDb();
        var rows = await db.AuditLogs.ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].CorrelationId.Should().Be("p2");
    }

    [Fact]
    public async Task Drainer_FlushFailure_ArchivesBatch()
    {
        // R0188 — when the primary flush throws, the drainer MUST spill the batch
        // to IAuditArchive instead of dropping it. This is the single test that
        // exercises the new failure seam end-to-end.
        var queue = new AuditWriteQueue();
        using var harness = new PoisonDrainerHarness(queue);

        var r1 = NewRecord("p1");
        var r2 = NewRecord("p2");
        queue.TryEnqueue(r1).Should().BeTrue();
        queue.TryEnqueue(r2).Should().BeTrue();

        await harness.Drainer.FlushOnceAsync(CancellationToken.None);

        await harness.Archive.Received(1).ArchiveAsync(
            Arg.Is<IReadOnlyList<AuditEventRecord>>(list =>
                list.Count == 2
                && list[0].CorrelationId == "p1"
                && list[1].CorrelationId == "p2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Queue_TryEnqueue_WhenFull_ReturnsFalse()
    {
        // Arrange
        var queue = new AuditWriteQueue();
        for (var i = 0; i < AuditWriteQueue.Capacity; i++)
        {
            queue.TryEnqueue(NewRecord(i.ToString())).Should().BeTrue();
        }

        // Act
        var overflow = queue.TryEnqueue(NewRecord("overflow"));

        // Assert
        overflow.Should().BeFalse();
    }

    /// <summary>
    /// R0194 / SEC 047 — flushing into an empty AuditLog table MUST chain the
    /// first row from the genesis literal. Without this anchor the chain has no
    /// stable origin and the verifier cannot tell a clean first row from a
    /// snipped prefix.
    /// </summary>
    [Fact]
    public async Task Drainer_FlushFromEmpty_ChainsFromGenesis()
    {
        var queue = new AuditWriteQueue();
        using var harness = new DrainerHarness(queue);

        queue.TryEnqueue(NewRecord("first")).Should().BeTrue();

        await harness.Drainer.FlushOnceAsync(CancellationToken.None);

        var db = harness.NewDb();
        var rows = await db.AuditLogs.OrderBy(a => a.Id).ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].PrevHash.Should().Be("GENESIS", "first-ever row anchors to the genesis literal.");
        rows[0].RowHash.Should().HaveLength(64);
    }

    /// <summary>
    /// R0194 / SEC 047 — a second flush MUST chain from the LAST persisted row's
    /// hash, not restart at genesis. The drainer reads the chain tail on every
    /// flush so the replay job can resume from the same point.
    /// </summary>
    [Fact]
    public async Task Drainer_FlushBuildsOnExistingTail_PrevHashChainsCorrectly()
    {
        var queue = new AuditWriteQueue();
        using var harness = new DrainerHarness(queue);

        // First flush — seeds the chain.
        queue.TryEnqueue(NewRecord("seed")).Should().BeTrue();
        await harness.Drainer.FlushOnceAsync(CancellationToken.None);

        // Second flush — must chain from the seeded row's hash.
        queue.TryEnqueue(NewRecord("next")).Should().BeTrue();
        await harness.Drainer.FlushOnceAsync(CancellationToken.None);

        var db = harness.NewDb();
        var rows = await db.AuditLogs.OrderBy(a => a.Id).ToListAsync();
        rows.Should().HaveCount(2);
        rows[1].PrevHash.Should().Be(rows[0].RowHash,
            "the second flush must read the chain tail and chain its first row from there.");
    }

    /// <summary>
    /// R0194 / SEC 047 — within a single flush batch the chain is built in
    /// submit order (sorted by <see cref="AuditEventRecord.EventAtUtc"/>), with
    /// each row's <c>PrevHash</c> equal to the previous row's <c>RowHash</c>.
    /// </summary>
    [Fact]
    public async Task Drainer_BatchOfThree_ChainsInSubmitOrder()
    {
        var queue = new AuditWriteQueue();
        using var harness = new DrainerHarness(queue);

        queue.TryEnqueue(NewRecord("c1")).Should().BeTrue();
        queue.TryEnqueue(NewRecord("c2")).Should().BeTrue();
        queue.TryEnqueue(NewRecord("c3")).Should().BeTrue();

        await harness.Drainer.FlushOnceAsync(CancellationToken.None);

        var db = harness.NewDb();
        var rows = await db.AuditLogs.OrderBy(a => a.Id).ToListAsync();
        rows.Should().HaveCount(3);
        rows[0].PrevHash.Should().Be("GENESIS");
        rows[1].PrevHash.Should().Be(rows[0].RowHash);
        rows[2].PrevHash.Should().Be(rows[1].RowHash);
    }

    private static AuditEventRecord NewRecord(string correlationId, string eventCode = "TEST.EVT")
        => new(
            EventCode: eventCode,
            Severity: AuditSeverity.Information,
            ActorId: "actor",
            TargetEntity: "Entity",
            TargetEntityId: 1L,
            DetailsJson: "{}",
            SourceIp: "127.0.0.1",
            CorrelationId: correlationId,
            EventAtUtc: ClockNow);

    /// <summary>
    /// Test harness that wires the drainer with a fresh in-memory db per scope, an
    /// NSubstitute IMLogClient, and an IServiceScopeFactory that produces both on
    /// every flush. The same database name is shared across scopes so we can assert
    /// the persisted rows from the outside.
    /// </summary>
    private sealed class DrainerHarness : IDisposable
    {
        private readonly string _dbName = $"cnas-drainer-{Guid.NewGuid():N}";
        private readonly ServiceProvider _provider;

        public AuditDrainer Drainer { get; }
        public IMLogClient Mlog { get; }
        public IAuditArchive Archive { get; }

        public DrainerHarness(AuditWriteQueue queue)
        {
            Mlog = Substitute.For<IMLogClient>();
            Mlog.AppendAsync(Arg.Any<MLogEntry>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));
            Archive = Substitute.For<IAuditArchive>();

            var services = new ServiceCollection();
            services.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            services.AddSingleton(Mlog);

            _provider = services.BuildServiceProvider();
            Drainer = new AuditDrainer(
                queue,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Archive,
                NullLogger<AuditDrainer>.Instance);
        }

        public CnasDbContext NewDb() => _provider.CreateScope()
            .ServiceProvider.GetRequiredService<CnasDbContext>();

        public void Dispose() => _provider.Dispose();
    }

    /// <summary>
    /// Variant harness whose <see cref="ICnasDbContext"/> throws on
    /// <see cref="ICnasDbContext.SaveChangesAsync"/> while <see cref="PoisonNextFlush"/>
    /// is <c>true</c>. Exercises the "flush failed — drop batch and keep running" path.
    /// </summary>
    private sealed class PoisonDrainerHarness : IDisposable
    {
        private readonly string _dbName = $"cnas-drainer-poison-{Guid.NewGuid():N}";
        private readonly ServiceProvider _provider;
        private readonly PoisonGate _gate = new();

        public AuditDrainer Drainer { get; }
        public IMLogClient Mlog { get; }
        public IAuditArchive Archive { get; }
        public bool PoisonNextFlush
        {
            get => _gate.Enabled;
            set => _gate.Enabled = value;
        }

        public PoisonDrainerHarness(AuditWriteQueue queue)
        {
            Mlog = Substitute.For<IMLogClient>();
            Mlog.AppendAsync(Arg.Any<MLogEntry>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));
            Archive = Substitute.For<IAuditArchive>();
            Archive.ArchiveAsync(Arg.Any<IReadOnlyList<AuditEventRecord>>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddSingleton(_gate);
            services.AddDbContext<PoisonCnasDbContext>(opts => opts
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<PoisonCnasDbContext>());
            services.AddSingleton(Mlog);

            _provider = services.BuildServiceProvider();
            Drainer = new AuditDrainer(
                queue,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Archive,
                NullLogger<AuditDrainer>.Instance);
        }

        public PoisonCnasDbContext NewDb() => _provider.CreateScope()
            .ServiceProvider.GetRequiredService<PoisonCnasDbContext>();

        public void Dispose() => _provider.Dispose();
    }

    /// <summary>
    /// Shared toggle that the poisoned DbContext consults on every
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> call.
    /// </summary>
    private sealed class PoisonGate
    {
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// CnasDbContext subclass that throws on <see cref="SaveChangesAsync"/> while the
    /// gate is enabled. Re-uses the in-memory schema of the base context untouched.
    /// </summary>
    private sealed class PoisonCnasDbContext : CnasDbContext
    {
        private readonly PoisonGate _gate;

        public PoisonCnasDbContext(DbContextOptions<PoisonCnasDbContext> options, PoisonGate gate)
            : base(options)
        {
            _gate = gate;
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (_gate.Enabled)
            {
                throw new InvalidOperationException("simulated DB failure");
            }
            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
