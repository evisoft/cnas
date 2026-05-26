using System.Linq;
using Cnas.Ps.Application.Migration;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Migration;
using Cnas.Ps.Infrastructure.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Tests.Migration;

/// <summary>
/// R2430 / R2431 / R2433 / TOR M4 — tests for the migration importer.
/// </summary>
public sealed class MigrationImporterTests
{
    [Fact]
    public async Task ImportAsync_DryRun_CreatesUncommittedStagingRows()
    {
        using var db = MigrationTestHelpers.CreateContext();
        var audit = MigrationTestHelpers.NewAuditCapturing(out var codes);
        var src = new InMemoryMigrationSource();
        var plan = await MigrationTestHelpers.SeedPlanAsync(db);
        src.Seed(plan.PlanCode, new[]
        {
            MigrationTestHelpers.NewRecord("fp-1", ("k", "v1")),
            MigrationTestHelpers.NewRecord("fp-2", ("k", "v2")),
        });
        var importer = MigrationTestHelpers.NewImporter(db, src, audit);

        var result = await importer.ImportAsync($"SQID-{plan.Id}", MigrationTriggerKind.DryRun);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsDryRun.Should().BeTrue();
        result.Value.TotalSourceRowsSeen.Should().Be(2);

        var staging = await db.MigrationStagingRows.ToListAsync();
        staging.Should().HaveCount(2);
        staging.All(r => !r.IsCommitted).Should().BeTrue();

        var findings = await db.MigrationFindings.ToListAsync();
        findings.Should().NotBeEmpty();

        codes.Should().Contain(IMigrationImporter.AuditRunCompleted);
    }

    [Fact]
    public async Task ImportAsync_Apply_FlipsIsCommitted()
    {
        using var db = MigrationTestHelpers.CreateContext();
        var audit = MigrationTestHelpers.NewAuditCapturing(out _);
        var src = new InMemoryMigrationSource();
        var plan = await MigrationTestHelpers.SeedPlanAsync(db);
        src.Seed(plan.PlanCode, new[]
        {
            MigrationTestHelpers.NewRecord("fp-1", ("k", "v1")),
        });
        var importer = MigrationTestHelpers.NewImporter(db, src, audit);

        var result = await importer.ImportAsync($"SQID-{plan.Id}", MigrationTriggerKind.Manual);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsDryRun.Should().BeFalse();
        var staging = await db.MigrationStagingRows.SingleAsync();
        staging.IsCommitted.Should().BeTrue();
        staging.CommittedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ImportAsync_EmptySource_CompletesWithZeroRows()
    {
        using var db = MigrationTestHelpers.CreateContext();
        var audit = MigrationTestHelpers.NewAuditCapturing(out _);
        var src = new InMemoryMigrationSource();
        var plan = await MigrationTestHelpers.SeedPlanAsync(db);
        // Do not seed any records.
        var importer = MigrationTestHelpers.NewImporter(db, src, audit);

        var result = await importer.ImportAsync($"SQID-{plan.Id}", MigrationTriggerKind.DryRun);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalSourceRowsSeen.Should().Be(0);
        result.Value.Status.Should().Be(MigrationRunStatus.Completed.ToString());
    }

    [Fact]
    public async Task ImportAsync_PeakHourGateBlocked_ReturnsConflict()
    {
        using var db = MigrationTestHelpers.CreateContext();
        var audit = MigrationTestHelpers.NewAuditCapturing(out _);
        var src = new InMemoryMigrationSource();
        var plan = await MigrationTestHelpers.SeedPlanAsync(db);
        src.Seed(plan.PlanCode, new[] { MigrationTestHelpers.NewRecord("fp-1") });
        var importer = MigrationTestHelpers.NewImporter(db, src, audit, peakGate: new AlwaysSkipPeakHourGate());

        var result = await importer.ImportAsync($"SQID-{plan.Id}", MigrationTriggerKind.Manual);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(IMigrationImporter.PeakHourGateBlockedCode);
    }
}
