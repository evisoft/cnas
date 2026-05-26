using Cnas.Ps.Application.Migration;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Tests.Migration;

/// <summary>
/// R2430 / TOR M4 — tests for the migration plan service lifecycle.
/// </summary>
public sealed class MigrationPlanServiceTests
{
    [Fact]
    public async Task CreateAsync_HappyPath_PersistsAndEmitsCriticalAudit()
    {
        using var db = MigrationTestHelpers.CreateContext();
        var audit = MigrationTestHelpers.NewAuditCapturing(out var codes);
        var svc = MigrationTestHelpers.NewPlanService(db, audit);

        var input = new MigrationPlanCreateInputDto(
            PlanCode: "LEGACY_PENSIONS_2026",
            Title: "Legacy pensions migration",
            Description: "Pre-2024 cohort.",
            SourceKind: "InMemoryTest",
            TargetEntityName: "Pension",
            MappingDescriptorJson: null,
            BatchSize: 500);

        var result = await svc.CreateAsync(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.PlanCode.Should().Be("LEGACY_PENSIONS_2026");
        result.Value.Status.Should().Be(MigrationPlanStatus.Draft.ToString());
        codes.Should().Contain(IMigrationPlanService.AuditPlanCreated);

        var persisted = await db.MigrationPlans.SingleAsync();
        persisted.PlanCode.Should().Be("LEGACY_PENSIONS_2026");
    }

    [Fact]
    public async Task ApproveAsync_FromDraft_TransitionsToApproved()
    {
        using var db = MigrationTestHelpers.CreateContext();
        var audit = MigrationTestHelpers.NewAuditCapturing(out _);
        var svc = MigrationTestHelpers.NewPlanService(db, audit);
        var plan = await MigrationTestHelpers.SeedPlanAsync(db, status: MigrationPlanStatus.Draft);

        var result = await svc.ApproveAsync($"SQID-{plan.Id}");

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(MigrationPlanStatus.Approved.ToString());
    }

    [Fact]
    public async Task ApproveAsync_FromActive_Fails()
    {
        using var db = MigrationTestHelpers.CreateContext();
        var audit = MigrationTestHelpers.NewAuditCapturing(out _);
        var svc = MigrationTestHelpers.NewPlanService(db, audit);
        var plan = await MigrationTestHelpers.SeedPlanAsync(db, status: MigrationPlanStatus.Active);

        var result = await svc.ApproveAsync($"SQID-{plan.Id}");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(IMigrationPlanService.InvalidTransitionCode);
    }

    [Fact]
    public async Task ArchiveAsync_FromActive_TransitionsToArchived()
    {
        using var db = MigrationTestHelpers.CreateContext();
        var audit = MigrationTestHelpers.NewAuditCapturing(out _);
        var svc = MigrationTestHelpers.NewPlanService(db, audit);
        var plan = await MigrationTestHelpers.SeedPlanAsync(db, status: MigrationPlanStatus.Active);

        var result = await svc.ArchiveAsync(
            $"SQID-{plan.Id}",
            new MigrationPlanReasonInputDto("Cohort migrated successfully."));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(MigrationPlanStatus.Archived.ToString());
    }
}
