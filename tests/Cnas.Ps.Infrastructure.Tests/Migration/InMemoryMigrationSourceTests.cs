using System.Linq;
using Cnas.Ps.Application.Migration;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Migration;

namespace Cnas.Ps.Infrastructure.Tests.Migration;

/// <summary>
/// R2430 / R2431 / TOR M4 — tests for the in-memory migration source.
/// </summary>
public sealed class InMemoryMigrationSourceTests
{
    /// <summary>CA1861 — hoisted expected-fingerprint list.</summary>
    private static readonly string[] ExpectedFingerprints = ["fp-1", "fp-2"];

    [Fact]
    public async Task StreamAsync_ReturnsSeededRecords()
    {
        var src = new InMemoryMigrationSource();
        src.Seed("PLAN_X", new[]
        {
            MigrationTestHelpers.NewRecord("fp-1"),
            MigrationTestHelpers.NewRecord("fp-2"),
        });
        var plan = new MigrationPlan { PlanCode = "PLAN_X", BatchSize = 100 };

        var list = new System.Collections.Generic.List<MigrationSourceRecord>();
        await foreach (var r in src.StreamAsync(plan))
        {
            list.Add(r);
        }

        list.Should().HaveCount(2);
        list.Select(r => r.SourceFingerprint).Should().BeEquivalentTo(ExpectedFingerprints);
    }

    [Fact]
    public async Task CountAsync_ReturnsSeededCount()
    {
        var src = new InMemoryMigrationSource();
        src.Seed("PLAN_Y", new[]
        {
            MigrationTestHelpers.NewRecord("a"),
            MigrationTestHelpers.NewRecord("b"),
            MigrationTestHelpers.NewRecord("c"),
        });
        var plan = new MigrationPlan { PlanCode = "PLAN_Y" };

        var count = await src.CountAsync(plan);

        count.Should().Be(3);
    }
}
