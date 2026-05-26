using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Search;

/// <summary>
/// R0522 / TOR CF 03.03 — tests for the <see cref="IFullTextSearchEngine"/> abstraction
/// and its two seed adapters. The Postgres ILIKE adapter is exercised against the
/// InMemory provider (it transparently degrades to a client-side
/// <see cref="DiacriticFolding"/> + substring check); the
/// <see cref="NotImplementedExternalSearchEngine"/> adapter must throw on first call.
/// </summary>
public sealed class FullTextSearchEngineTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Static array referenced by the happy-path test to satisfy CA1861.</summary>
    private static readonly string[] ExpectedPopescuIds = { "SQID-1", "SQID-3" };

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-fts-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private static ISqidService BuildSqids()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        return sqids;
    }

    [Fact]
    public async Task PostgresIlike_SearchAsync_ReturnsMatchingIdsAndCount()
    {
        await using var db = CreateContext();
        for (long i = 1; i <= 3; i++)
        {
            db.Solicitants.Add(new Solicitant
            {
                Id = i,
                NationalId = $"2000000000{i:D3}",
                NationalIdHash = $"h-{i}",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = i == 2 ? "Maria Iordache" : $"Ion Popescu {i}",
                CreatedAtUtc = ClockNow,
                IsActive = true,
            });
        }
        await db.SaveChangesAsync();

        var engine = new PostgresIlikeFullTextSearchEngine(db, BuildSqids());

        var result = await engine.SearchAsync(
            indexName: "Solicitant",
            query: "Popescu",
            skip: 0,
            take: 10,
            ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
        result.Value.Ids.Should().BeEquivalentTo(ExpectedPopescuIds);
    }

    [Fact]
    public async Task NotImplementedExternal_SearchAsync_ThrowsNotImplementedException()
    {
        var engine = new NotImplementedExternalSearchEngine();

        var act = async () => await engine.SearchAsync(
            indexName: "Solicitant",
            query: "anything",
            skip: 0,
            take: 10,
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<NotImplementedException>();
    }

    [Fact]
    public void PostgresIlikeFullTextSearchEngine_EngineName_IsStable()
    {
        var engine = new NotImplementedExternalSearchEngine();
        engine.EngineName.Should().Be("NotImplementedExternal");
    }
}
