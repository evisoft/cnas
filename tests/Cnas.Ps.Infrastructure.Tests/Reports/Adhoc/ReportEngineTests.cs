using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Exports;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.Reports;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Exports;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Qbe;
using Cnas.Ps.Infrastructure.QueryBudget;
using Cnas.Ps.Infrastructure.Services.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Reports.Adhoc;

/// <summary>
/// R0156 / TOR CF 09.02 / FLEX 003 — execution-side tests for <see cref="ReportEngine"/>.
/// </summary>
public class ReportEngineTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Reused seeds (CA1861 — no inline new[] arrays).</summary>
    private static readonly string[] IdName = ["Id", "DisplayName"];

    private static readonly string[] IdNameKind = ["Id", "DisplayName", "Kind"];

    private static readonly string[] DefaultEngineSelected = ["Id", "DisplayName"];

    private static readonly string[] OrderingTestFields = ["DisplayName", "CreatedAtUtc"];

    private static readonly ReportOrderingDto[] OrderingTestOrdering =
    [
        new ReportOrderingDto("DisplayName", ReportOrderingDto.Asc),
        new ReportOrderingDto("CreatedAtUtc", ReportOrderingDto.Desc),
    ];

    private static readonly string[] KindOnly = ["Kind"];

    private static readonly string[] GroupByExpectedColumns = ["Kind", "count"];

    private static readonly string[] EngineOwnerRoles = ["cnas-user"];

    private static readonly ReportOrderingDto[] DefaultEngineOrdering =
        [new ReportOrderingDto("DisplayName", ReportOrderingDto.Asc)];

    /// <summary>Ordered seed used by the multi-column ordering test.</summary>
    private static readonly (string name, DateTime created)[] OrderingCustomSeed =
    [
        ("Beta", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        ("Alpha", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
        ("Alpha", new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)),
    ];

    [Fact]
    public async Task RunAsync_ReturnsSelectedColumnsAndMatchingRows()
    {
        var h = Harness.Create();
        await h.SeedSolicitantsAsync(3);
        var template = await h.SeedTemplateAsync(selectedFields: IdName);

        var result = await h.Engine.RunAsync(template.Id, skip: 0, take: 50);

        result.IsSuccess.Should().BeTrue();
        result.Value.Columns.Should().BeEquivalentTo(IdName);
        result.Value.Rows.Should().HaveCount(3);
        // Cell values projected by reflection.
        result.Value.Rows[0].Cells.Should().ContainKey("DisplayName");
    }

    [Fact]
    public async Task RunAsync_EnforcesBudgetGate_ReturnsQueryTooBroad()
    {
        var h = Harness.Create(budgetOverride: 2);
        await h.SeedSolicitantsAsync(10);
        var template = await h.SeedTemplateAsync();

        var result = await h.Engine.RunAsync(template.Id, skip: 0, take: 50);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.QueryTooBroad);
    }

    [Fact]
    public async Task RunAsync_WritesReportRun_WithSuccessOutcome()
    {
        var h = Harness.Create();
        await h.SeedSolicitantsAsync(2);
        var template = await h.SeedTemplateAsync();

        var result = await h.Engine.RunAsync(template.Id, skip: 0, take: 50);

        result.IsSuccess.Should().BeTrue();
        var run = await h.Db.ReportRuns.SingleAsync();
        run.OutcomeCode.Should().Be("Success");
        run.ReportTemplateId.Should().Be(template.Id);
        run.RowCount.Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_BudgetRejection_WritesReportRun_WithBudgetExceededOutcome()
    {
        var h = Harness.Create(budgetOverride: 1);
        await h.SeedSolicitantsAsync(5);
        var template = await h.SeedTemplateAsync();

        var result = await h.Engine.RunAsync(template.Id, skip: 0, take: 50);

        result.IsFailure.Should().BeTrue();
        var run = await h.Db.ReportRuns.SingleAsync();
        run.OutcomeCode.Should().Be("BudgetExceeded");
    }

    [Fact]
    public async Task ExportAsync_Csv_ReturnsBytes_AndRowCountMatchesRun()
    {
        var h = Harness.Create();
        await h.SeedSolicitantsAsync(3);
        var template = await h.SeedTemplateAsync(selectedFields: IdName);

        var export = await h.Engine.ExportAsync(template.Id, ExportFormat.Csv);

        export.IsSuccess.Should().BeTrue();
        export.Value.Should().NotBeEmpty();
        var csv = Encoding.UTF8.GetString(export.Value);
        // Three data rows + at least one header row.
        csv.Should().Contain("DisplayName");

        var run = await h.Db.ReportRuns.SingleAsync(r => r.OutcomeCode == "Success");
        run.RowCount.Should().Be(3);
    }

    [Fact]
    public async Task RunAsync_MultiColumnOrdering_ProducesExpectedOrder()
    {
        var h = Harness.Create();
        await h.SeedSolicitantsCustomAsync(OrderingCustomSeed);

        var template = await h.SeedTemplateAsync(
            selectedFields: OrderingTestFields,
            ordering: OrderingTestOrdering);

        var result = await h.Engine.RunAsync(template.Id, skip: 0, take: 50);

        result.IsSuccess.Should().BeTrue();
        var names = result.Value.Rows.Select(r => r.Cells["DisplayName"] as string).ToList();
        names.Should().Equal("Alpha", "Alpha", "Beta");

        // For the two Alphas, the one with the LATER CreatedAtUtc comes first (Desc).
        var alphaDates = result.Value.Rows
            .Where(r => (string?)r.Cells["DisplayName"] == "Alpha")
            .Select(r => (DateTime)r.Cells["CreatedAtUtc"]!)
            .ToList();
        alphaDates[0].Should().BeAfter(alphaDates[1]);
    }

    [Fact]
    public async Task RunAsync_GroupByField_ReturnsOneRowPerGroup_WithCountAggregate()
    {
        var h = Harness.Create();
        // Two NaturalPerson, one LegalPerson — grouped by Kind.
        await h.SeedSolicitantsByKindAsync(natural: 2, legal: 1);

        var template = await h.SeedTemplateAsync(
            selectedFields: KindOnly,
            groupBy: "Kind",
            ordering: Array.Empty<ReportOrderingDto>());

        var result = await h.Engine.RunAsync(template.Id, skip: 0, take: 50);

        result.IsSuccess.Should().BeTrue();
        result.Value.Columns.Should().BeEquivalentTo(GroupByExpectedColumns);
        result.Value.Rows.Should().HaveCount(2);
        // Both groups produce a "count" column with integer cell.
        result.Value.Rows.Should().AllSatisfy(r =>
        {
            r.Cells.Should().ContainKey("count");
            r.Cells["count"].Should().BeOfType<int>();
        });
    }

    // ─────────────────────── Harness ───────────────────────

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class SingleBudgetPolicy(int budget) : IQueryBudgetPolicy
    {
        public QueryBudgetPolicy GetForRegistry(string registry) =>
            new(registry, budget, Array.Empty<RefinementHintRule>());
    }

    private sealed class Harness
    {
        public const long OwnerUserId = 9101L;

        public required CnasDbContext Db { get; init; }
        public required ReportEngine Engine { get; init; }
        public required ICallerContext Caller { get; init; }
        public required IGridExporter Exporter { get; init; }

        public static Harness Create(int? budgetOverride = null)
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-reportengine-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(OwnerUserId);
            caller.UserSqid.Returns("SQID-OWNER");
            caller.Roles.Returns(EngineOwnerRoles);

            var clock = new StubClock(ClockNow);
            var schemas = new QbeRegistrySchemaProvider();
            var qbeConverter = new QbeToLinqConverter(schemas);

            IQueryBudgetPolicy policy = budgetOverride is { } b
                ? new SingleBudgetPolicy(b)
                : new StaticQueryBudgetPolicy(NullLogger<StaticQueryBudgetPolicy>.Instance);
            var budget = new QueryBudgetService(policy, NullLogger<QueryBudgetService>.Instance);

            var renderers = new IGridExportRenderer[]
            {
                new CsvGridExportRenderer(),
            };
            var exporter = new GridExporter(renderers, NullLogger<GridExporter>.Instance);

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var engine = new ReportEngine(db, caller, clock, schemas, qbeConverter, budget, exporter, audit);
            return new Harness { Db = db, Engine = engine, Caller = caller, Exporter = exporter };
        }

        public async Task SeedSolicitantsAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Db.Solicitants.Add(new Solicitant
                {
                    NationalId = $"2{i:D12}",
                    NationalIdHash = $"h-{i}",
                    Kind = ApplicantKind.NaturalPerson,
                    DisplayName = $"Person {i:D5}",
                    CreatedAtUtc = ClockNow,
                    IsActive = true,
                });
            }
            await Db.SaveChangesAsync();
        }

        public async Task SeedSolicitantsByKindAsync(int natural, int legal)
        {
            var idx = 0;
            for (var i = 0; i < natural; i++, idx++)
            {
                Db.Solicitants.Add(new Solicitant
                {
                    NationalId = $"2{idx:D12}",
                    NationalIdHash = $"h-{idx}",
                    Kind = ApplicantKind.NaturalPerson,
                    DisplayName = $"NP {idx:D3}",
                    CreatedAtUtc = ClockNow,
                    IsActive = true,
                });
            }
            for (var i = 0; i < legal; i++, idx++)
            {
                Db.Solicitants.Add(new Solicitant
                {
                    NationalId = $"2{idx:D12}",
                    NationalIdHash = $"h-{idx}",
                    Kind = ApplicantKind.LegalPerson,
                    DisplayName = $"LP {idx:D3}",
                    CreatedAtUtc = ClockNow,
                    IsActive = true,
                });
            }
            await Db.SaveChangesAsync();
        }

        public async Task SeedSolicitantsCustomAsync(IEnumerable<(string name, DateTime created)> rows)
        {
            var idx = 0;
            foreach (var (name, created) in rows)
            {
                Db.Solicitants.Add(new Solicitant
                {
                    NationalId = $"2{idx:D12}",
                    NationalIdHash = $"h-{idx}",
                    Kind = ApplicantKind.NaturalPerson,
                    DisplayName = name,
                    CreatedAtUtc = created,
                    IsActive = true,
                });
                idx++;
            }
            await Db.SaveChangesAsync();
        }

        public async Task<ReportTemplate> SeedTemplateAsync(
            IReadOnlyList<string>? selectedFields = null,
            IReadOnlyList<ReportOrderingDto>? ordering = null,
            string? groupBy = null,
            bool isShared = false)
        {
            var fields = selectedFields ?? DefaultEngineSelected;
            var order = ordering ?? DefaultEngineOrdering;
            var filter = new QbeFilterDto(QbeFilter.CombinatorAnd, Array.Empty<QbeConditionDto>());
            var template = new ReportTemplate
            {
                Code = $"report.test.{Guid.NewGuid():N}".Substring(0, 30),
                Name = "Test",
                Description = null,
                Registry = QueryBudgetRegistries.Solicitant,
                SelectedFieldsJson = System.Text.Json.JsonSerializer.Serialize(fields),
                FilterJson = System.Text.Json.JsonSerializer.Serialize(filter),
                OrderingJson = System.Text.Json.JsonSerializer.Serialize(order),
                GroupByField = groupBy,
                OwnerUserId = OwnerUserId,
                IsShared = isShared,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.ReportTemplates.Add(template);
            await Db.SaveChangesAsync();
            return template;
        }
    }
}
