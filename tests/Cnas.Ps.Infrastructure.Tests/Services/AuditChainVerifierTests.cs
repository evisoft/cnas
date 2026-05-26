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
/// R0194 / SEC 047 — integration tests for <see cref="AuditChainVerifier"/>. The
/// verifier walks the AuditLog rows in <c>Id</c> order, recomputing the expected
/// row hash at each step from the previous row's stored hash. Any tampering
/// (edited <c>DetailsJson</c>, edited <c>PrevHash</c>, edited <c>EventAtUtc</c> …)
/// must be reported with the first broken row's id and a stable reason code.
/// </summary>
/// <remarks>
/// Member of <see cref="CnasMeterCollection"/> — VerifyAsync emits on the static
/// meter (<c>cnas.audit.chain.verified</c>) so cross-test parallelism must be off.
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public class AuditChainVerifierTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// An empty AuditLog table is — by definition — a valid chain (the verifier
    /// has nothing to disprove). <see cref="AuditChainVerificationReport.CheckedCount"/>
    /// is zero and no broken-row metadata is set.
    /// </summary>
    [Fact]
    public async Task Verify_EmptyTable_ReturnsValid()
    {
        await using var harness = new Harness();
        var verifier = harness.Verifier;

        var report = await verifier.VerifyAsync();

        report.IsSuccess.Should().BeTrue();
        report.Value.IsValid.Should().BeTrue();
        report.Value.CheckedCount.Should().Be(0);
        report.Value.FirstBrokenRowId.Should().BeNull();
        report.Value.FirstBrokenReason.Should().BeNull();
    }

    /// <summary>
    /// A single row chained from the literal genesis anchor must verify
    /// cleanly — the recipe and the verifier agree on the same string.
    /// </summary>
    [Fact]
    public async Task Verify_SingleRow_FromGenesis_ReturnsValid()
    {
        await using var harness = new Harness();
        harness.SeedChain(NewRecord("c1"));

        var report = await harness.Verifier.VerifyAsync();

        report.IsSuccess.Should().BeTrue();
        report.Value.IsValid.Should().BeTrue();
        report.Value.CheckedCount.Should().Be(1);
    }

    /// <summary>
    /// Three correctly-chained rows must verify cleanly. The CheckedCount equals
    /// the row count exactly — proves the walker did not bail out early.
    /// </summary>
    [Fact]
    public async Task Verify_MultipleRows_AllIntact_ReturnsValid()
    {
        await using var harness = new Harness();
        harness.SeedChain(
            NewRecord("c1"),
            NewRecord("c2"),
            NewRecord("c3"));

        var report = await harness.Verifier.VerifyAsync();

        report.IsSuccess.Should().BeTrue();
        report.Value.IsValid.Should().BeTrue();
        report.Value.CheckedCount.Should().Be(3);
    }

    /// <summary>
    /// Tampering with <see cref="AuditLog.DetailsJson"/> AFTER the chain was
    /// written invalidates the row's <c>RowHash</c> — the recomputed digest no
    /// longer matches the stored one. The verifier must surface the row id and
    /// the <c>RowHashMismatch</c> reason.
    /// </summary>
    [Fact]
    public async Task Verify_TamperedDetailsJson_ReturnsRowHashMismatch_AtTamperedRow()
    {
        await using var harness = new Harness();
        var ids = harness.SeedChain(
            NewRecord("c1"),
            NewRecord("c2"),
            NewRecord("c3"));

        // Tamper with row 2 (the middle one) AFTER the chain was committed.
        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
            var row = await db.AuditLogs.SingleAsync(a => a.Id == ids[1]);
            row.DetailsJson = "{\"tampered\":true}";
            await db.SaveChangesAsync();
        }

        var report = await harness.Verifier.VerifyAsync();

        report.IsSuccess.Should().BeTrue();
        report.Value.IsValid.Should().BeFalse();
        report.Value.FirstBrokenRowId.Should().Be(ids[1]);
        report.Value.FirstBrokenReason.Should().Be("RowHashMismatch");
    }

    /// <summary>
    /// Tampering with <see cref="AuditLog.PrevHash"/> breaks the linkage
    /// independently of the row hash itself — the verifier reports
    /// <c>PrevHashMismatch</c> with the row id whose link is broken.
    /// </summary>
    [Fact]
    public async Task Verify_TamperedPrevHash_ReturnsPrevHashMismatch_AtTamperedRow()
    {
        await using var harness = new Harness();
        var ids = harness.SeedChain(
            NewRecord("c1"),
            NewRecord("c2"));

        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
            var row = await db.AuditLogs.SingleAsync(a => a.Id == ids[1]);
            row.PrevHash = new string('0', 64);
            await db.SaveChangesAsync();
        }

        var report = await harness.Verifier.VerifyAsync();

        report.IsSuccess.Should().BeTrue();
        report.Value.IsValid.Should().BeFalse();
        report.Value.FirstBrokenRowId.Should().Be(ids[1]);
        report.Value.FirstBrokenReason.Should().Be("PrevHashMismatch");
    }

    /// <summary>
    /// Tampering with <see cref="AuditLog.EventAtUtc"/> shifts the canonical
    /// form of the row and therefore the recomputed hash — surfaces as a
    /// <c>RowHashMismatch</c>.
    /// </summary>
    [Fact]
    public async Task Verify_TamperedEventAtUtc_DetectsBreak()
    {
        await using var harness = new Harness();
        var ids = harness.SeedChain(NewRecord("c1"));

        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
            var row = await db.AuditLogs.SingleAsync(a => a.Id == ids[0]);
            row.EventAtUtc = row.EventAtUtc.AddSeconds(1);
            await db.SaveChangesAsync();
        }

        var report = await harness.Verifier.VerifyAsync();

        report.IsSuccess.Should().BeTrue();
        report.Value.IsValid.Should().BeFalse();
        report.Value.FirstBrokenRowId.Should().Be(ids[0]);
        report.Value.FirstBrokenReason.Should().Be("RowHashMismatch");
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
    /// Fixture wiring a single InMemory shard shared by writer + reader contexts.
    /// Seeds a hash-chained sequence of <see cref="AuditLog"/> rows via direct EF
    /// inserts so the verifier has a deterministic input to validate.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _dbName = $"cnas-chain-{Guid.NewGuid():N}";
        private readonly ServiceProvider _provider;

        public Harness()
        {
            var services = new ServiceCollection();
            services.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddDbContext<CnasReadOnlyDbContext>(opts => opts
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            services.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasReadOnlyDbContext>());
            services.AddScoped<IAuditChainVerifier, AuditChainVerifier>();
            _provider = services.BuildServiceProvider();
        }

        public IAuditChainVerifier Verifier => _provider.CreateScope()
            .ServiceProvider.GetRequiredService<IAuditChainVerifier>();

        public IServiceScope CreateScope() => _provider.CreateScope();

        /// <summary>
        /// Inserts <paramref name="records"/> as a correctly-hashed chain starting
        /// from <c>"GENESIS"</c>. Returns the assigned row ids in insert order so
        /// individual tests can mutate a specific row.
        /// </summary>
        public IReadOnlyList<long> SeedChain(params AuditEventRecord[] records)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
            var prev = "GENESIS";
            var rows = new List<AuditLog>(records.Length);
            foreach (var r in records)
            {
                var rowHash = AuditFlushProjector.ComputeRowHash(r, prev);
                var row = AuditFlushProjector.ToAuditLog(r);
                row.PrevHash = prev;
                row.RowHash = rowHash;
                rows.Add(row);
                prev = rowHash;
            }
            db.AuditLogs.AddRange(rows);
            db.SaveChanges();
            return rows.Select(r => r.Id).ToList();
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
        }
    }
}
