using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Archive;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Archive;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0332 / TOR CF 12.02 — pins the archive metadata summariser. The shape we
/// guarantee end-to-end:
/// <list type="bullet">
///   <item>An empty system reports zero across every tab + null last-updated.</item>
///   <item>A mixed-state seed reports the per-tab Active / Archived split correctly.</item>
///   <item>Decisions are a filtered view of Documents (kind = Decision).</item>
///   <item>LastUpdatedUtc reflects the most-recently-touched row across active + archived.</item>
/// </list>
/// </summary>
public sealed class ArchiveMetadataServiceTests
{
    /// <summary>Deterministic clock instant used across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Empty system — every tab reports zero active + zero archived + null
    /// last-updated. Sanity check the dto stays well-formed when there is
    /// nothing to count.
    /// </summary>
    [Fact]
    public async Task GetSummaryAsync_EmptySystem_ReturnsAllZeros()
    {
        var db = CreateContext();
        IArchiveMetadataService svc = new ArchiveMetadataService(db);

        var result = await svc.GetSummaryAsync();

        result.IsSuccess.Should().BeTrue();
        var s = result.Value!;
        foreach (var tab in s.AllTabs())
        {
            tab.TotalActive.Should().Be(0);
            tab.TotalArchived.Should().Be(0);
            tab.LastUpdatedUtc.Should().BeNull();
        }
    }

    /// <summary>
    /// Mixed-state — 2 active + 1 archived Contributor; 3 active + 0 archived
    /// InsuredPerson. Every count surfaces independently; the unrelated tabs
    /// stay at zero so the per-tab COUNT scope is correct.
    /// </summary>
    [Fact]
    public async Task GetSummaryAsync_MixedState_CountsActiveVsArchived()
    {
        var db = CreateContext();

        db.Contributors.AddRange(
            NewContributor("1003600012346", "SRL One", isActive: true),
            NewContributor("1003600012347", "SRL Two", isActive: true),
            NewContributor("1003600012348", "SRL Three", isActive: false));
        db.InsuredPersons.AddRange(
            NewInsured("2003600012346", "Doe", "Ion"),
            NewInsured("2003600012347", "Doe", "Maria"),
            NewInsured("2003600012348", "Doe", "Vasile"));
        await db.SaveChangesAsync();

        IArchiveMetadataService svc = new ArchiveMetadataService(db);
        var result = await svc.GetSummaryAsync();

        result.IsSuccess.Should().BeTrue();
        var s = result.Value!;
        s.Contributors.TotalActive.Should().Be(2);
        s.Contributors.TotalArchived.Should().Be(1);
        s.InsuredPersons.TotalActive.Should().Be(3);
        s.InsuredPersons.TotalArchived.Should().Be(0);
        s.Dossiers.TotalActive.Should().Be(0);
        s.Documents.TotalActive.Should().Be(0);
    }

    /// <summary>
    /// Decisions tab is a filtered view of Documents — counting Documents
    /// rows where <c>Kind == DocumentKind.Decision</c>. A mix of Decision +
    /// Certificate rows must surface only the Decision count on the Decisions
    /// tab while both contribute to the Documents tab.
    /// </summary>
    [Fact]
    public async Task GetSummaryAsync_DecisionsTabFiltersDocumentKindDecision()
    {
        var db = CreateContext();
        db.Documents.AddRange(
            NewDocument("Decision A", DocumentKind.Decision),
            NewDocument("Decision B", DocumentKind.Decision),
            NewDocument("Cert", DocumentKind.Certificate),
            NewDocument("Extract", DocumentKind.Extract));
        await db.SaveChangesAsync();

        IArchiveMetadataService svc = new ArchiveMetadataService(db);
        var result = await svc.GetSummaryAsync();

        result.IsSuccess.Should().BeTrue();
        var s = result.Value!;
        s.Decisions.TotalActive.Should().Be(2, "only DocumentKind.Decision rows count toward the Decisions tab");
        s.Documents.TotalActive.Should().Be(4, "every Document row counts toward the Documents tab");
    }

    /// <summary>
    /// LastUpdatedUtc — the summary picks the most-recently-touched row's
    /// timestamp. UpdatedAtUtc when set, otherwise CreatedAtUtc. We seed three
    /// contributors with staggered timestamps and assert the latest wins.
    /// </summary>
    [Fact]
    public async Task GetSummaryAsync_LastUpdatedReflectsMostRecentChange()
    {
        var db = CreateContext();
        // Oldest — created and never touched.
        db.Contributors.Add(NewContributor(
            idno: "1003600012346", denumire: "Oldest", isActive: true,
            createdAt: ClockNow.AddDays(-30)));
        // Middle — created then touched a week ago.
        db.Contributors.Add(NewContributor(
            idno: "1003600012347", denumire: "Middle", isActive: true,
            createdAt: ClockNow.AddDays(-20), updatedAt: ClockNow.AddDays(-7)));
        // Newest — just created today; UpdatedAtUtc null so CreatedAtUtc wins.
        db.Contributors.Add(NewContributor(
            idno: "1003600012348", denumire: "Newest", isActive: true,
            createdAt: ClockNow));
        await db.SaveChangesAsync();

        IArchiveMetadataService svc = new ArchiveMetadataService(db);
        var result = await svc.GetSummaryAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value!.Contributors.LastUpdatedUtc.Should().Be(ClockNow,
            "the newest contributor row (created today) defines the LastUpdatedUtc badge");
    }

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    /// <returns>An isolated converter-less context.</returns>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-archive-meta-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Helper — minimal Contributor seed row.</summary>
    private static Contributor NewContributor(
        string idno, string denumire, bool isActive,
        DateTime? createdAt = null, DateTime? updatedAt = null) =>
        new()
        {
            CreatedAtUtc = createdAt ?? ClockNow,
            UpdatedAtUtc = updatedAt,
            Idno = idno,
            Denumire = denumire,
            IsActive = isActive,
        };

    /// <summary>Helper — minimal InsuredPerson seed row.</summary>
    private static InsuredPerson NewInsured(string idnp, string lastName, string firstName) =>
        new()
        {
            CreatedAtUtc = ClockNow,
            Idnp = idnp,
            LastName = lastName,
            FirstName = firstName,
            BirthDate = new DateOnly(2000, 1, 1),
            RegisteredAtUtc = ClockNow,
            IsActive = true,
        };

    /// <summary>Helper — minimal Document seed row.</summary>
    private static Document NewDocument(string title, DocumentKind kind) =>
        new()
        {
            CreatedAtUtc = ClockNow,
            Title = title,
            Kind = kind,
            MimeType = "application/pdf",
            StorageObjectKey = $"obj-{Guid.NewGuid():N}",
            StorageBucket = "cnas-test",
            ContentSha256Hex = new string('a', 64),
            IsActive = true,
        };
}
