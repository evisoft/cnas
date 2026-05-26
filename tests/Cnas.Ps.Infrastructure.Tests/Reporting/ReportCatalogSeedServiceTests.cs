using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Common;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Reporting;

/// <summary>
/// R1900-R1905 / iter-145 — unit tests for
/// <see cref="ReportCatalogSeedService"/>. Pins four invariants:
/// <list type="bullet">
///   <item>The descriptor table covers at least 50 codes (R1900 acceptance).</item>
///   <item>Refresh inserts one row per descriptor on first run.</item>
///   <item>Refresh is idempotent — a second run reports zero inserts / updates.</item>
///   <item>List returns the persisted rows ordered by code with Sqid-encoded ids.</item>
///   <item>Metadata roundtrips faithfully through DB persistence.</item>
/// </list>
/// </summary>
public sealed class ReportCatalogSeedServiceTests
{
    /// <summary>Deterministic UTC clock instant.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Descriptors_AreAtLeast50()
    {
        ReportCatalogDescriptors.All.Should().HaveCountGreaterThanOrEqualTo(
            50,
            "R1900 — the seeded catalog must cover ≥50 Annex 6 + stock reports.");
    }

    [Fact]
    public async Task Refresh_OnEmptyTable_InsertsOneRowPerDescriptor()
    {
        var harness = Harness.Create();

        var result = await harness.Service.RefreshAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value!.Inserted.Should().Be(ReportCatalogDescriptors.All.Count);
        result.Value.Updated.Should().Be(0);
        result.Value.Unchanged.Should().Be(0);
        result.Value.Total.Should().Be(ReportCatalogDescriptors.All.Count);

        var persistedCount = await harness.Db.Reports.CountAsync();
        persistedCount.Should().Be(ReportCatalogDescriptors.All.Count);
    }

    [Fact]
    public async Task Refresh_IsIdempotent()
    {
        var harness = Harness.Create();

        var first = await harness.Service.RefreshAsync();
        var second = await harness.Service.RefreshAsync();

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value!.Inserted.Should().Be(0,
            "the second refresh must NOT re-insert any rows.");
        second.Value.Updated.Should().Be(0,
            "the second refresh must NOT mark any row as drifted.");
        second.Value.Unchanged.Should().Be(ReportCatalogDescriptors.All.Count);
    }

    [Fact]
    public async Task Refresh_PersistsAllMetadataColumnsFaithfully()
    {
        var harness = Harness.Create();
        await harness.Service.RefreshAsync();

        // Pick one well-known code — RPT-PEN-ACTIVE — and verify every metadata column.
        var descriptor = ReportCatalogDescriptors.All.Single(d => d.Code == "RPT-PEN-ACTIVE");
        var persisted = await harness.Db.Reports.SingleAsync(r => r.Code == "RPT-PEN-ACTIVE");

        persisted.NameRo.Should().Be(descriptor.NameRo);
        persisted.Purpose.Should().Be(descriptor.Purpose);
        persisted.Audience.Should().Be(descriptor.Audience);
        persisted.Frequency.Should().Be(descriptor.Frequency);
        persisted.ParameterSchemaJson.Should().Be(descriptor.ParametersJson);
        persisted.ColumnsJson.Should().Be(descriptor.ColumnsJson);
        persisted.RbacRole.Should().Be(descriptor.RbacRole);
        persisted.Schedule.Should().Be(descriptor.Schedule);
        persisted.OutputFormatsJson.Should().Be(descriptor.OutputFormatsJson);
        persisted.Category.Should().Be(descriptor.Category);
        persisted.DefaultFormat.Should().Be(descriptor.DefaultFormat);
    }

    [Fact]
    public async Task Refresh_UpsertsOnDrift()
    {
        var harness = Harness.Create();
        // First run inserts everything.
        await harness.Service.RefreshAsync();

        // Mutate one row to simulate drift.
        var row = await harness.Db.Reports.SingleAsync(r => r.Code == "AUDIT_LOG");
        row.NameRo = "drifted-name";
        row.Purpose = "drifted-purpose";
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.RefreshAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value!.Inserted.Should().Be(0);
        result.Value.Updated.Should().Be(1, "only the drifted row should be upserted.");
        // The metadata should match the descriptor again.
        var refreshed = await harness.Db.Reports.SingleAsync(r => r.Code == "AUDIT_LOG");
        var descriptor = ReportCatalogDescriptors.All.Single(d => d.Code == "AUDIT_LOG");
        refreshed.NameRo.Should().Be(descriptor.NameRo);
        refreshed.Purpose.Should().Be(descriptor.Purpose);
    }

    [Fact]
    public async Task List_ReturnsPersistedRowsOrderedByCode_WithSqidIds()
    {
        var harness = Harness.Create();
        await harness.Service.RefreshAsync();

        var listResult = await harness.Service.ListAsync();

        listResult.IsSuccess.Should().BeTrue();
        listResult.Value!.Items.Should().NotBeEmpty();
        listResult.Value.Total.Should().Be(ReportCatalogDescriptors.All.Count);

        // Ordering — Code ascending.
        listResult.Value.Items
            .Select(i => i.Code)
            .Should()
            .BeInAscendingOrder(StringComparer.Ordinal);

        // Sqid encoding — non-empty + not the raw integer.
        foreach (var item in listResult.Value.Items)
        {
            item.Id.Should().NotBeNullOrWhiteSpace();
            long.TryParse(item.Id, out _).Should().BeFalse(
                "the catalog Id must be Sqid-encoded per CLAUDE.md RULE 3.");
        }
    }

    [Fact]
    public async Task List_FiltersByCategoryAndFrequency()
    {
        var harness = Harness.Create();
        await harness.Service.RefreshAsync();

        var auditOnly = await harness.Service.ListAsync(category: "AuditSecurity");

        auditOnly.IsSuccess.Should().BeTrue();
        auditOnly.Value!.Items.Should().NotBeEmpty();
        auditOnly.Value.Items
            .Should()
            .OnlyContain(r => r.Category == "AuditSecurity");

        var monthlyOnly = await harness.Service.ListAsync(frequency: "Monthly");

        monthlyOnly.IsSuccess.Should().BeTrue();
        monthlyOnly.Value!.Items
            .Should()
            .OnlyContain(r => r.Frequency == "Monthly");
    }

    [Fact]
    public async Task Refresh_EmitsCriticalAuditEvent()
    {
        var harness = Harness.Create();

        await harness.Service.RefreshAsync();

        await harness.Audit.Received(1).RecordAsync(
            IReportCatalogSeedService.AuditCatalogRefreshed,
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(Report),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-rpt-catalog-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required ReportCatalogSeedService Service { get; init; }
        public required IAuditService Audit { get; init; }

        public static Harness Create()
        {
            var db = CreateContext();
            IReadOnlyCnasDbContext readDb = db;

            var sqids = new SqidService(Options.Create(new SqidOptions
            {
                Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
                MinLength = 6,
            }));
            var clock = new StubClock(ClockNow);
            var caller = Substitute.For<ICallerContext>();
            caller.UserSqid.Returns("SQID-ADMIN");
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("test-correlation");

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

            var service = new ReportCatalogSeedService(db, readDb, clock, sqids, caller, audit);

            return new Harness { Db = db, Service = service, Audit = audit };
        }
    }
}
