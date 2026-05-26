using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.PublicCatalog;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.QueryBudget;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0502 / R0504 / R0505 — service-level vertical-slice tests for
/// <see cref="PublicCatalogService"/>. Exercises sorting, the diacritic-aware
/// Q filter, the Category filter, the budget guard, and the CSV export
/// formatting against the InMemory provider.
/// </summary>
public sealed class PublicCatalogServiceTests
{
    /// <summary>Builds a fresh InMemory <see cref="CnasDbContext"/> instance.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-publiccatalog-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Assembles the SUT with a real budget service + InMemory db.</summary>
    private static (PublicCatalogService Svc, CnasDbContext Db) Build(int? budgetOverride = null)
    {
        var db = CreateContext();
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        IQueryBudgetPolicy policy = budgetOverride is { } b
            ? new SingleBudgetPolicy(b)
            : new StaticQueryBudgetPolicy(NullLogger<StaticQueryBudgetPolicy>.Instance);
        var budget = new QueryBudgetService(policy, NullLogger<QueryBudgetService>.Instance);
        return (new PublicCatalogService(db, sqids, budget), db);
    }

    /// <summary>Inserts a single passport with the provided overrides.</summary>
    private static async Task SeedAsync(
        CnasDbContext db,
        string code,
        string nameRo,
        string? descriptionRo = null,
        string? category = null,
        DateTime? createdAtUtc = null,
        DateTime? updatedAtUtc = null,
        bool isCurrent = true,
        bool isActive = true)
    {
        db.ServicePassports.Add(new ServicePassport
        {
            Code = code,
            NameRo = nameRo,
            DescriptionRo = descriptionRo ?? $"Descrierea pentru {nameRo}",
            Category = category,
            WorkflowCode = "WF-TEST",
            CreatedAtUtc = createdAtUtc ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = updatedAtUtc,
            IsCurrent = isCurrent,
            IsActive = isActive,
        });
        await db.SaveChangesAsync();
    }

    // ───────── Sorting ─────────

    [Fact]
    public async Task ListAsync_DefaultSortRelevance_OrdersByScoreDescThenNameAsc()
    {
        var (svc, db) = Build();
        // Use distinctive descriptions that DO NOT accidentally contain the
        // query token "pen" (the Romanian word "pentru" embeds "pen", so the
        // default seed-helper description must be overridden everywhere).
        //   P-A "Pensia anticipată"     — NameRo starts with "Pens" → score 3
        //   P-B "Compensații pensionari" — NameRo contains "pens"  → score 2
        //   P-C "Indemnizație unică"    — Desc contains "pen"      → score 1
        //   P-D "Alocații copii"        — neither hits              → score 0
        await SeedAsync(db, "P-A", "Pensia anticipată", descriptionRo: "Ajutor lunar.");
        await SeedAsync(db, "P-B", "Compensații pensionari", descriptionRo: "Sprijin lunar.");
        await SeedAsync(db, "P-C", "Indemnizație unică", descriptionRo: "Ajutor pen anual.");
        await SeedAsync(db, "P-D", "Alocații copii", descriptionRo: "Ajutor lunar.");

        var result = await svc.ListAsync(new PublicCatalogListQueryDto(Q: "pen"));

        result.IsSuccess.Should().BeTrue();
        var codes = result.Value.Items.Select(i => i.Code).ToList();
        codes.Should().Equal("P-A", "P-B", "P-C");
        codes.Should().NotContain("P-D");
    }

    [Fact]
    public async Task ListAsync_SortAlphabetical_OrdersByNameAscending()
    {
        var (svc, db) = Build();
        await SeedAsync(db, "S-1", "Zorro");
        await SeedAsync(db, "S-2", "Alfa");
        await SeedAsync(db, "S-3", "Mike");

        var result = await svc.ListAsync(new PublicCatalogListQueryDto(Sort: "Alphabetical"));

        result.IsSuccess.Should().BeTrue();
        var codes = result.Value.Items.Select(i => i.Code).ToList();
        codes.Should().Equal("S-2", "S-3", "S-1");
    }

    [Fact]
    public async Task ListAsync_SortCreated_OrdersByCreatedAtDesc()
    {
        var (svc, db) = Build();
        var baseDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedAsync(db, "C-OLDEST", "Old", createdAtUtc: baseDate.AddDays(-30));
        await SeedAsync(db, "C-MIDDLE", "Mid", createdAtUtc: baseDate.AddDays(-15));
        await SeedAsync(db, "C-NEWEST", "New", createdAtUtc: baseDate);

        var result = await svc.ListAsync(new PublicCatalogListQueryDto(Sort: "Created"));

        result.IsSuccess.Should().BeTrue();
        var codes = result.Value.Items.Select(i => i.Code).ToList();
        codes.Should().Equal("C-NEWEST", "C-MIDDLE", "C-OLDEST");
    }

    [Fact]
    public async Task ListAsync_SortUpdated_OrdersByUpdatedAtDesc_FallsBackToCreated()
    {
        var (svc, db) = Build();
        var baseDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        // U-1: never updated, created early.
        await SeedAsync(db, "U-1", "One", createdAtUtc: baseDate.AddDays(-30));
        // U-2: updated mid-window (most recent activity).
        await SeedAsync(db, "U-2", "Two", createdAtUtc: baseDate.AddDays(-20), updatedAtUtc: baseDate);
        // U-3: never updated, created most recently.
        await SeedAsync(db, "U-3", "Three", createdAtUtc: baseDate.AddDays(-5));

        var result = await svc.ListAsync(new PublicCatalogListQueryDto(Sort: "Updated"));

        result.IsSuccess.Should().BeTrue();
        var codes = result.Value.Items.Select(i => i.Code).ToList();
        codes.Should().Equal("U-2", "U-3", "U-1");
    }

    [Fact]
    public async Task ListAsync_TakeCappedAt200_EvenWhenClientSendsLarger()
    {
        var (svc, db) = Build();
        for (int i = 0; i < 5; i++)
        {
            await SeedAsync(db, $"K-{i:D3}", $"Service {i:D3}");
        }

        var result = await svc.ListAsync(new PublicCatalogListQueryDto(Sort: "Alphabetical", Take: 500));

        result.IsSuccess.Should().BeTrue();
        result.Value.PageSize.Should().Be(200);
    }

    // ───────── Filters ─────────

    [Fact]
    public async Task ListAsync_CategoryFilter_LimitsResultSet()
    {
        var (svc, db) = Build();
        await SeedAsync(db, "F-1", "Alpha", category: "PENSIONS");
        await SeedAsync(db, "F-2", "Bravo", category: "FAMILY");
        await SeedAsync(db, "F-3", "Charlie", category: "PENSIONS");

        var result = await svc.ListAsync(new PublicCatalogListQueryDto(
            Category: "PENSIONS",
            Sort: "Alphabetical"));

        result.IsSuccess.Should().BeTrue();
        var codes = result.Value.Items.Select(i => i.Code).ToList();
        codes.Should().Equal("F-1", "F-3");
    }

    [Fact]
    public async Task ListAsync_DiacriticFolded_QueryMatchesFoldedName()
    {
        var (svc, db) = Build();
        await SeedAsync(db, "D-1", "Alocații copii");
        await SeedAsync(db, "D-2", "Indemnizație");
        await SeedAsync(db, "D-3", "Pensia");

        var result = await svc.ListAsync(new PublicCatalogListQueryDto(Q: "alocatii"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(i => i.Code == "D-1");
    }

    // ───────── Budget gate ─────────

    [Fact]
    public async Task ListAsync_OverBudget_NoFilters_ReturnsQueryTooBroad()
    {
        var (svc, db) = Build(budgetOverride: 10);
        for (int i = 0; i < 25; i++)
        {
            await SeedAsync(db, $"BIG-{i:D3}", $"Item {i:D3}");
        }

        var result = await svc.ListAsync(new PublicCatalogListQueryDto());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.QueryTooBroad);
        svc.LastBudgetVerdict.Should().NotBeNull();
        svc.LastBudgetVerdict!.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task ListAsync_OverBudget_WithQFilter_StillAllowedByRequiredHintSatisfaction()
    {
        // Confirms that supplying Q satisfies the "Required" hint — the verdict
        // would still refuse purely on row count, so we use a tight budget but
        // ensure Q matches only one row so the count is back below the budget.
        var (svc, db) = Build(budgetOverride: 10);
        for (int i = 0; i < 25; i++)
        {
            await SeedAsync(db, $"BIG-{i:D3}", $"Item {i:D3}");
        }
        // Add a unique-named row Q will match exclusively.
        await SeedAsync(db, "UNIQ", "Pensia anticipată unică");

        var result = await svc.ListAsync(new PublicCatalogListQueryDto(Q: "anticipata unica"));

        result.IsSuccess.Should().BeTrue();
        svc.LastBudgetVerdict!.Allowed.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(i => i.Code == "UNIQ");
    }

    [Fact]
    public async Task ListAsync_DraftAndInactivePassports_NotReturned()
    {
        var (svc, db) = Build();
        // Visible: IsCurrent=true, IsActive=true
        await SeedAsync(db, "VIS", "Visible");
        // Hidden: IsCurrent=false (historical revision)
        await SeedAsync(db, "HIST", "Historical", isCurrent: false);
        // Hidden: IsActive=false (soft-deleted)
        await SeedAsync(db, "DEAD", "SoftDeleted", isActive: false);

        var result = await svc.ListAsync(new PublicCatalogListQueryDto(Sort: "Alphabetical"));

        result.IsSuccess.Should().BeTrue();
        var codes = result.Value.Items.Select(i => i.Code).ToList();
        codes.Should().Equal("VIS");
    }

    // ───────── Export CSV ─────────

    [Fact]
    public async Task ExportCsvAsync_ProducesValidHeaderAndQuotedFields()
    {
        var (svc, db) = Build();
        await SeedAsync(db, "CSV-1", "Pensie",
            descriptionRo: "Linie cu, virgulă și \"ghilimele\"",
            category: "PENSIONS",
            createdAtUtc: new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc));

        var result = await svc.ExportCsvAsync(new PublicCatalogListQueryDto(Sort: "Alphabetical"));

        result.IsSuccess.Should().BeTrue();
        var text = System.Text.Encoding.UTF8.GetString(result.Value);
        // Strip BOM for the header comparison.
        var withoutBom = text.TrimStart('﻿');
        var lines = withoutBom.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Be(PublicCatalogCsvWriter.Header);
        // Data row: Code,Name,Description (quoted because comma + quote),Category,Version,UpdatedAt
        lines[1].Should().StartWith("CSV-1,Pensie,");
        lines[1].Should().Contain("\"Linie cu, virgulă și \"\"ghilimele\"\"\"");
        lines[1].Should().Contain(",PENSIONS,");
    }

    [Fact]
    public async Task ExportCsvAsync_OverBudgetNoFilters_ReturnsQueryTooBroadFailure()
    {
        var (svc, db) = Build(budgetOverride: 5);
        for (int i = 0; i < 20; i++)
        {
            await SeedAsync(db, $"EX-{i:D3}", $"Item {i:D3}");
        }

        var result = await svc.ExportCsvAsync(new PublicCatalogListQueryDto());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.QueryTooBroad);
        svc.LastBudgetVerdict.Should().NotBeNull();
    }

    // ───────── Validation through service entrypoint ─────────

    [Fact]
    public async Task ListAsync_UnknownSort_ReturnsValidationFailed()
    {
        var (svc, _) = Build();

        var result = await svc.ListAsync(new PublicCatalogListQueryDto(Sort: "Foo"));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>Test stub that returns a single-budget policy regardless of registry.</summary>
    private sealed class SingleBudgetPolicy(int budget) : IQueryBudgetPolicy
    {
        public QueryBudgetPolicy GetForRegistry(string registry) =>
            new(registry, budget, Array.Empty<RefinementHintRule>());
    }
}
