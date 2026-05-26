using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Jobs;

/// <summary>
/// Tests for <see cref="AuditArchiveReplayJob"/> — the periodic replay of audit
/// batches that <see cref="AuditDrainer"/> spilled to <see cref="IAuditArchive"/>
/// after a primary-flush failure (R0188).
/// </summary>
/// <remarks>
/// Member of <see cref="CnasMeterCollection"/> — the replay job emits on the static
/// meter (<c>cnas.audit.replay.*</c>) so cross-test parallelism is suppressed.
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public class AuditArchiveReplayJobTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Execute_NoArchives_DoesNothing()
    {
        var harness = new Harness();
        harness.Archive.ListPendingAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ArchivedAuditBatchRef>>(Array.Empty<ArchivedAuditBatchRef>()));

        await harness.Job.Execute(FakeContext());

        await harness.Archive.DidNotReceive().ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Archive.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        (await harness.NewDb().AuditLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Execute_OneSuccessfulBatch_PersistsAndDeletes()
    {
        var harness = new Harness();
        var records = new[] { NewRecord("c1", "EVT.A"), NewRecord("c2", "EVT.B") };
        harness.SeedArchive("a-1", records);

        await harness.Job.Execute(FakeContext());

        var db = harness.NewDb();
        var rows = await db.AuditLogs.OrderBy(a => a.CorrelationId).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(r => r.CorrelationId).Should().Equal("c1", "c2");
        rows.Select(r => r.EventCode).Should().Equal("EVT.A", "EVT.B");

        await harness.Mlog.Received(2).AppendAsync(Arg.Any<MLogEntry>(), Arg.Any<CancellationToken>());
        await harness.Archive.Received(1).DeleteAsync("a-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_FailedFlush_LeavesArchiveInPlace()
    {
        // Poison the DB context so SaveChangesAsync throws. The job must catch,
        // log, and leave the archive ID UNDELETED for the next run.
        var harness = new Harness(poisonSaves: true);
        var records = new[] { NewRecord("c1") };
        harness.SeedArchive("a-2", records);

        await harness.Job.Execute(FakeContext());

        await harness.Archive.DidNotReceive().DeleteAsync("a-2", Arg.Any<CancellationToken>());
        (await harness.NewDb().AuditLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Execute_RespectsMaxReplayBatchesPerRun()
    {
        var harness = new Harness(maxPerRun: 2);

        // Seed 5 archives but cap is 2 → only 2 should be attempted.
        for (var i = 0; i < 5; i++)
        {
            harness.SeedArchive($"a-{i}", new[] { NewRecord($"c{i}") });
        }

        await harness.Job.Execute(FakeContext());

        var db = harness.NewDb();
        (await db.AuditLogs.CountAsync()).Should().Be(2);
        await harness.Archive.Received(2).DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_EmptyArchive_IsSweptWithoutWritingRows()
    {
        // An archive that returns an empty record list (e.g. quarantined as corrupt)
        // must still be deleted — leaving it on disk would cause the loop to retry
        // forever.
        var harness = new Harness();
        harness.SeedArchive("a-empty", Array.Empty<AuditEventRecord>());

        await harness.Job.Execute(FakeContext());

        (await harness.NewDb().AuditLogs.CountAsync()).Should().Be(0);
        await harness.Archive.Received(1).DeleteAsync("a-empty", Arg.Any<CancellationToken>());
    }

    private static IJobExecutionContext FakeContext()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
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
    /// Test harness wiring an NSubstitute <see cref="IAuditArchive"/>, an in-memory
    /// <see cref="CnasDbContext"/> (optionally poisoned), and a real
    /// <see cref="AuditArchiveReplayJob"/>.
    /// </summary>
    private sealed class Harness
    {
        private readonly ServiceProvider _provider;
        private readonly List<(string Id, IReadOnlyList<AuditEventRecord> Records)> _seeded = new();

        public IAuditArchive Archive { get; } = Substitute.For<IAuditArchive>();
        public IMLogClient Mlog { get; }
        public AuditArchiveReplayJob Job { get; }
        public string DbName { get; } = $"cnas-replay-{Guid.NewGuid():N}";

        public Harness(bool poisonSaves = false, int maxPerRun = 100)
        {
            Mlog = Substitute.For<IMLogClient>();
            Mlog.AppendAsync(Arg.Any<MLogEntry>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var services = new ServiceCollection();
            services.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(DbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            if (poisonSaves)
            {
                services.AddDbContext<PoisonCnasDbContext>(opts => opts
                    .UseInMemoryDatabase(DbName)
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
                services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<PoisonCnasDbContext>());
            }
            else
            {
                services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            }
            services.AddSingleton(Mlog);

            _provider = services.BuildServiceProvider();

            Archive.ListPendingAsync(Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<IReadOnlyList<ArchivedAuditBatchRef>>(
                    _seeded.Select(s => new ArchivedAuditBatchRef(s.Id, ClockNow, s.Records.Count)).ToList()));
            Archive.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var id = call.Arg<string>();
                    var match = _seeded.FirstOrDefault(s => s.Id == id);
                    return Task.FromResult(match.Records ?? Array.Empty<AuditEventRecord>());
                });
            Archive.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var id = call.Arg<string>();
                    _seeded.RemoveAll(s => s.Id == id);
                    return Task.CompletedTask;
                });

            Job = new AuditArchiveReplayJob(
                Archive,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                new Cnas.Ps.Infrastructure.Tests.Common.AllowAllPeakHourGate(),
                Options.Create(new AuditArchiveOptions { MaxReplayBatchesPerRun = maxPerRun }),
                NullLogger<AuditArchiveReplayJob>.Instance);
        }

        public void SeedArchive(string id, IReadOnlyList<AuditEventRecord> records)
            => _seeded.Add((id, records));

        public CnasDbContext NewDb()
        {
            var scope = _provider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        }
    }

    /// <summary>
    /// CnasDbContext subclass that always throws on <see cref="SaveChangesAsync"/>.
    /// Used by the "failed flush leaves archive in place" path.
    /// </summary>
    private sealed class PoisonCnasDbContext : CnasDbContext
    {
        public PoisonCnasDbContext(DbContextOptions<PoisonCnasDbContext> options) : base(options)
        {
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated replay failure");
    }
}
