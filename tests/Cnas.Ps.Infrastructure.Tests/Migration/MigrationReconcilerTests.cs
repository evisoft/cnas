using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Migration;

namespace Cnas.Ps.Infrastructure.Tests.Migration;

/// <summary>
/// R2433 / TOR M4 — tests for the migration reconciler.
/// </summary>
public sealed class MigrationReconcilerTests
{
    [Fact]
    public async Task ReconcileAsync_AllFingerprintsMatch_StatusPassed()
    {
        using var db = MigrationTestHelpers.CreateContext();
        var audit = MigrationTestHelpers.NewAuditCapturing(out _);
        var src = new InMemoryMigrationSource();
        var plan = await MigrationTestHelpers.SeedPlanAsync(db);
        src.Seed(plan.PlanCode, new[]
        {
            MigrationTestHelpers.NewRecord("fp-1"),
            MigrationTestHelpers.NewRecord("fp-2"),
        });
        var importer = MigrationTestHelpers.NewImporter(db, src, audit);
        var runResult = await importer.ImportAsync($"SQID-{plan.Id}", Cnas.Ps.Core.Domain.MigrationTriggerKind.Manual);
        runResult.IsSuccess.Should().BeTrue();

        var reconciler = MigrationTestHelpers.NewReconciler(db, src, audit);
        var result = await reconciler.ReconcileAsync(runResult.Value.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(ReconciliationStatus.Passed.ToString());
        result.Value.SourceRowCount.Should().Be(2);
        result.Value.TargetRowCount.Should().Be(2);
        result.Value.MissingInTargetCount.Should().Be(0);
        result.Value.UnexpectedInTargetCount.Should().Be(0);
    }

    [Fact]
    public async Task ReconcileAsync_SourceLargerThanStaging_StatusDiscrepancy()
    {
        using var db = MigrationTestHelpers.CreateContext();
        var audit = MigrationTestHelpers.NewAuditCapturing(out _);
        var src = new InMemoryMigrationSource();
        var plan = await MigrationTestHelpers.SeedPlanAsync(db);
        src.Seed(plan.PlanCode, new[]
        {
            MigrationTestHelpers.NewRecord("fp-1"),
            MigrationTestHelpers.NewRecord("fp-2"),
        });
        var importer = MigrationTestHelpers.NewImporter(db, src, audit);
        var runResult = await importer.ImportAsync($"SQID-{plan.Id}", Cnas.Ps.Core.Domain.MigrationTriggerKind.Manual);

        // After the run, mutate the source to include an extra fingerprint.
        src.Seed(plan.PlanCode, new[]
        {
            MigrationTestHelpers.NewRecord("fp-1"),
            MigrationTestHelpers.NewRecord("fp-2"),
            MigrationTestHelpers.NewRecord("fp-3"),
        });

        var reconciler = MigrationTestHelpers.NewReconciler(db, src, audit);
        var result = await reconciler.ReconcileAsync(runResult.Value.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(ReconciliationStatus.Discrepancy.ToString());
        result.Value.MissingInTargetCount.Should().Be(1);
    }
}
