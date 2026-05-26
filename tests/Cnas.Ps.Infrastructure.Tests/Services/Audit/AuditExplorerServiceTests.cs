using System.Globalization;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Application.Exports;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Exports;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Qbe;
using Cnas.Ps.Infrastructure.QueryBudget;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Services.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services.Audit;

/// <summary>
/// R0193 / TOR SEC 052 — service-level tests for
/// <see cref="AuditExplorerService"/>. Drives the SUT against an InMemory
/// <see cref="CnasDbContext"/> shared by writer + reader so seeded rows are
/// visible to the explorer pipeline.
/// </summary>
public sealed class AuditExplorerServiceTests
{
    private static readonly DateTime BaseUtc = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Sets up a self-contained InMemory harness wiring the explorer service +
    /// its real budget guard, QBE converter, CSV renderer, and audit service
    /// substitute. Returns the SUT + the writer DbContext so individual tests
    /// can seed rows.
    /// </summary>
    private static Harness Build(
        int? budgetOverride = null,
        IAuditArchive? archiveOverride = null,
        IAuditService? auditOverride = null,
        ICallerContext? callerOverride = null)
    {
        var dbName = $"cnas-audit-explorer-{Guid.NewGuid():N}";
        var writeOpts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var readOpts = new DbContextOptionsBuilder<CnasReadOnlyDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        var writeDb = new CnasDbContext(writeOpts);
        var readDb = new CnasReadOnlyDbContext(readOpts);

        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call =>
            "SQID-" + call.Arg<long>().ToString(CultureInfo.InvariantCulture));

        IQueryBudgetPolicy policy = budgetOverride is { } b
            ? new SingleBudgetPolicy(b)
            : new StaticQueryBudgetPolicy(NullLogger<StaticQueryBudgetPolicy>.Instance);
        var budget = new QueryBudgetService(policy, NullLogger<QueryBudgetService>.Instance);
        var qbeConverter = new QbeToLinqConverter(new QbeRegistrySchemaProvider());

        var csvRenderer = new CsvGridExportRenderer();
        var exporter = new GridExporter(
            new IGridExportRenderer[] { csvRenderer },
            NullLogger<GridExporter>.Instance);

        var archive = archiveOverride ?? Substitute.For<IAuditArchive>();
        var audit = auditOverride ?? Substitute.For<IAuditService>();
        var caller = callerOverride ?? NewCaller();

        var sut = new AuditExplorerService(
            readDb,
            writeDb,
            qbeConverter,
            budget,
            exporter,
            archive,
            audit,
            caller,
            sqids,
            NullLogger<AuditExplorerService>.Instance);

        return new Harness(sut, writeDb, readDb, archive, audit, caller);
    }

    private static ICallerContext NewCaller(string? userSqid = "admin-sqid", long? userId = 99L)
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns(userSqid);
        caller.UserId.Returns(userId);
        caller.SourceIp.Returns("127.0.0.1");
        caller.CorrelationId.Returns("corr-1");
        return caller;
    }

    private static AuditLog NewAuditLog(
        DateTime atUtc,
        string eventCode = "USER.LOGIN.SUCCESS",
        AuditSeverity severity = AuditSeverity.Information,
        string actorId = "42",
        string? targetEntity = "UserProfile",
        long? targetEntityId = 7L,
        string? sourceIp = "10.0.0.1",
        string? correlationId = "corr-x",
        string detailsJson = "{\"k\":\"v\"}",
        string prevHash = "GENESIS",
        string rowHash = "abc12345deadbeef")
    {
        return new AuditLog
        {
            CreatedAtUtc = atUtc,
            EventAtUtc = atUtc,
            Severity = severity,
            EventCode = eventCode,
            ActorId = actorId,
            TargetEntity = targetEntity,
            TargetEntityId = targetEntityId,
            SourceIp = sourceIp,
            CorrelationId = correlationId,
            DetailsJson = detailsJson,
            PrevHash = prevHash,
            RowHash = rowHash,
        };
    }

    [Fact]
    public async Task SearchAsync_DateRangeFilter_ScopesToWindow()
    {
        var h = Build();
        h.WriteDb.AuditLogs.Add(NewAuditLog(BaseUtc.AddDays(-3)));
        h.WriteDb.AuditLogs.Add(NewAuditLog(BaseUtc.AddDays(-1)));
        h.WriteDb.AuditLogs.Add(NewAuditLog(BaseUtc.AddDays(1)));
        await h.WriteDb.SaveChangesAsync();

        var result = await h.Sut.SearchAsync(new AuditLogSearchInput(
            FromUtc: BaseUtc.AddDays(-2),
            ToUtc: BaseUtc.AddDays(2)));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
        result.Value.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task SearchAsync_QbeFilter_OnEventCode_NarrowsResults()
    {
        var h = Build();
        h.WriteDb.AuditLogs.Add(NewAuditLog(BaseUtc.AddMinutes(-10), eventCode: "USER.LOGIN.SUCCESS"));
        h.WriteDb.AuditLogs.Add(NewAuditLog(BaseUtc.AddMinutes(-5), eventCode: "USER.LOGIN.FAIL"));
        h.WriteDb.AuditLogs.Add(NewAuditLog(BaseUtc.AddMinutes(-1), eventCode: "USER.LOGIN.SUCCESS"));
        await h.WriteDb.SaveChangesAsync();

        var qbe = new QbeFilterDto(
            Combinator: "AND",
            Conditions: new[]
            {
                new QbeConditionDto("EventCode", "Equals", "USER.LOGIN.SUCCESS"),
            });
        var result = await h.Sut.SearchAsync(new AuditLogSearchInput(Filter: qbe));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
        result.Value.Items.Should().OnlyContain(r => r.EventCode == "USER.LOGIN.SUCCESS");
    }

    [Fact]
    public async Task SearchAsync_OverBudget_ReturnsQueryTooBroad()
    {
        // Budget = 2; seed 10 rows; no filters → must refuse.
        var h = Build(budgetOverride: 2);
        for (var i = 0; i < 10; i++)
        {
            h.WriteDb.AuditLogs.Add(NewAuditLog(BaseUtc.AddMinutes(i), correlationId: $"c-{i}"));
        }
        await h.WriteDb.SaveChangesAsync();

        var result = await h.Sut.SearchAsync(new AuditLogSearchInput());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.QueryTooBroad);
        h.Sut.LastBudgetVerdict.Should().NotBeNull();
        h.Sut.LastBudgetVerdict!.Allowed.Should().BeFalse();
        h.Sut.LastBudgetVerdict.EstimatedRowCount.Should().Be(10);
    }

    [Fact]
    public async Task SearchAsync_TakeAboveCap_RejectedByValidator()
    {
        var h = Build();

        var result = await h.Sut.SearchAsync(new AuditLogSearchInput(Take: 1000));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task SearchAsync_RowsCarrySqidsAndHashPrefixes()
    {
        var h = Build();
        h.WriteDb.AuditLogs.Add(NewAuditLog(
            BaseUtc,
            prevHash: "GENESIS",
            rowHash: "ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789"));
        await h.WriteDb.SaveChangesAsync();

        var result = await h.Sut.SearchAsync(new AuditLogSearchInput());

        result.IsSuccess.Should().BeTrue();
        var row = result.Value.Items.Single();
        row.Id.Should().StartWith("SQID-");
        // GENESIS literal is shorter than 8 chars → trimmed to lowercase verbatim.
        row.PrevHashHex.Should().Be("genesis");
        // Long hash truncated to first 8 lowercase chars.
        row.RowHashHex.Should().Be("abcdef01");
        row.RowHashHex.Length.Should().Be(AuditExplorerService.HashPrefixLength);
    }

    [Fact]
    public async Task ExportAsync_Csv_ReturnsBytes_AndRowCountMatches()
    {
        var h = Build();
        h.WriteDb.AuditLogs.Add(NewAuditLog(BaseUtc.AddMinutes(-2), correlationId: "c1"));
        h.WriteDb.AuditLogs.Add(NewAuditLog(BaseUtc.AddMinutes(-1), correlationId: "c2"));
        await h.WriteDb.SaveChangesAsync();

        var result = await h.Sut.ExportAsync(new AuditLogSearchInput(), ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        result.Value.Content.Length.Should().BeGreaterThan(0);
        result.Value.ContentType.Should().StartWith("text/csv");
        // Two rows + one header row = at least 3 line endings present.
        var text = System.Text.Encoding.UTF8.GetString(result.Value.Content);
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.Should().BeGreaterOrEqualTo(3);
    }

    [Fact]
    public async Task ExportAsync_XlsxRendererMissing_ReturnsExportFormatNotSupported()
    {
        // The harness wires only the CSV renderer — XLSX dispatch must fail loudly.
        var h = Build();
        h.WriteDb.AuditLogs.Add(NewAuditLog(BaseUtc));
        await h.WriteDb.SaveChangesAsync();

        var result = await h.Sut.ExportAsync(new AuditLogSearchInput(), ExportFormat.Xlsx);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ExportFormatNotSupported);
    }

    [Fact]
    public async Task ExportAsync_OverBudget_ReturnsQueryTooBroad()
    {
        var h = Build(budgetOverride: 2);
        for (var i = 0; i < 10; i++)
        {
            h.WriteDb.AuditLogs.Add(NewAuditLog(BaseUtc.AddMinutes(i), correlationId: $"c-{i}"));
        }
        await h.WriteDb.SaveChangesAsync();

        var result = await h.Sut.ExportAsync(new AuditLogSearchInput(), ExportFormat.Csv);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.QueryTooBroad);
    }

    [Fact]
    public async Task ImportArchiveAsync_SkipsExistingRows_OnReImport()
    {
        // First import inserts 3 rows; second import of the same archive must
        // skip them all (idempotency primitive on the natural composite key).
        var existingRecord = new AuditEventRecord(
            EventCode: "TEST.IMPORTED",
            Severity: AuditSeverity.Information,
            ActorId: "import-actor",
            TargetEntity: "Unit",
            TargetEntityId: 1L,
            DetailsJson: "{\"a\":1}",
            SourceIp: null,
            CorrelationId: "corr-imp",
            EventAtUtc: BaseUtc);
        var batch = new[]
        {
            existingRecord,
            existingRecord with { TargetEntityId = 2L, EventAtUtc = BaseUtc.AddMinutes(1) },
            existingRecord with { TargetEntityId = 3L, EventAtUtc = BaseUtc.AddMinutes(2) },
        };
        var archive = Substitute.For<IAuditArchive>();
        archive.ReadAsync("file-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AuditEventRecord>>(batch));

        var h = Build(archiveOverride: archive);

        // First import — inserts all three.
        var first = await h.Sut.ImportArchiveAsync("file-1");
        first.IsSuccess.Should().BeTrue();
        first.Value.RowsImported.Should().Be(3);
        first.Value.RowsSkipped.Should().Be(0);

        // Second import — the writeDb is shared so every row is now a duplicate.
        var second = await h.Sut.ImportArchiveAsync("file-1");
        second.IsSuccess.Should().BeTrue();
        second.Value.RowsImported.Should().Be(0);
        second.Value.RowsSkipped.Should().Be(3);
    }

    [Fact]
    public async Task ImportArchiveAsync_EmitsCriticalAuditEvent()
    {
        var record = new AuditEventRecord(
            EventCode: "FOO.BAR",
            Severity: AuditSeverity.Information,
            ActorId: "actor",
            TargetEntity: "Entity",
            TargetEntityId: 9L,
            DetailsJson: "{}",
            SourceIp: null,
            CorrelationId: null,
            EventAtUtc: BaseUtc);
        var archive = Substitute.For<IAuditArchive>();
        archive.ReadAsync("file-x", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AuditEventRecord>>(new[] { record }));
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(),
                Arg.Any<AuditSeverity>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<long?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        var h = Build(archiveOverride: archive, auditOverride: audit);

        var result = await h.Sut.ImportArchiveAsync("file-x");

        result.IsSuccess.Should().BeTrue();
        await audit.Received(1).RecordAsync(
            AuditExplorerService.AuditArchiveImportedEventCode,
            AuditSeverity.Critical,
            Arg.Any<string>(),
            "AuditLog",
            Arg.Any<long?>(),
            Arg.Is<string>(s => s.Contains("file-x", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportArchiveAsync_MissingArchive_ReturnsNotFound()
    {
        var archive = Substitute.For<IAuditArchive>();
        archive.ReadAsync("ghost-file", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AuditEventRecord>>(Array.Empty<AuditEventRecord>()));
        var h = Build(archiveOverride: archive);

        var result = await h.Sut.ImportArchiveAsync("ghost-file");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    /// <summary>Test stub returning a single-budget policy for every registry.</summary>
    private sealed class SingleBudgetPolicy(int budget) : IQueryBudgetPolicy
    {
        public QueryBudgetPolicy GetForRegistry(string registry) =>
            new(registry, budget, Array.Empty<RefinementHintRule>());
    }

    /// <summary>
    /// Test harness fixture carrying the SUT, the writer + reader DB contexts,
    /// and the supplied substitutes so individual tests can compose seed data
    /// and assertions against the same scope.
    /// </summary>
    private sealed class Harness
    {
        public AuditExplorerService Sut { get; }
        public CnasDbContext WriteDb { get; }
        public CnasReadOnlyDbContext ReadDb { get; }
        public IAuditArchive Archive { get; }
        public IAuditService Audit { get; }
        public ICallerContext Caller { get; }

        public Harness(
            AuditExplorerService sut,
            CnasDbContext writeDb,
            CnasReadOnlyDbContext readDb,
            IAuditArchive archive,
            IAuditService audit,
            ICallerContext caller)
        {
            Sut = sut;
            WriteDb = writeDb;
            ReadDb = readDb;
            Archive = archive;
            Audit = audit;
            Caller = caller;
        }
    }
}
