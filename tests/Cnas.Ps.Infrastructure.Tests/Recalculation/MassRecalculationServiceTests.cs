using Cnas.Ps.Application.Recalculation;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Tests.Recalculation;

/// <summary>
/// R1503 / TOR §3.7-D — integration-style tests for
/// <c>MassRecalculationService</c> and its
/// <c>MassRecalculationOrchestrator</c>. Exercises the DryRun /
/// PeakHourGate / Apply / Reject / Apply-Approved flows end-to-end against
/// the EF InMemory store with a test-only
/// <see cref="RecalculationTestHelpers.FakeBenefitRecalculationStrategy"/>.
/// </summary>
public sealed class MassRecalculationServiceTests
{
    /// <summary>Static-readonly fake-decision id lists — required by CA1861 to avoid per-call array literals.</summary>
    private static readonly long[] ThreeDecisionIds = { 1001L, 1002L, 1003L };

    /// <summary>Two-element decision id list for the Apply / Reject scenarios.</summary>
    private static readonly long[] TwoDecisionIdsApply = { 2001L, 2002L };

    /// <summary>Two-element decision id list for the Reject-then-ApplyApproved scenario.</summary>
    private static readonly long[] TwoDecisionIdsReject = { 3001L, 3002L };

    /// <summary>Single-element decision id list for the Reject-on-Applied-row conflict scenario.</summary>
    private static readonly long[] OneDecisionIdApplied = { 4001L };

    /// <summary>Empty strategy list used wherever the orchestrator needs the no-strategy path.</summary>
    private static readonly IBenefitRecalculationStrategy[] NoStrategies = Array.Empty<IBenefitRecalculationStrategy>();

    [Fact]
    public async Task DryRun_WithoutStrategy_RecordsSkippedRowsWithNoStrategyReason()
    {
        using var db = RecalculationTestHelpers.CreateContext();
        var evt = await RecalculationTestHelpers.SeedReadyEventAsync(db);
        var svc = RecalculationTestHelpers.NewMassRecalcService(
            db,
            RecalculationTestHelpers.NewAuditCapturing(out _),
            strategies: NoStrategies);

        var r = await svc.StartDryRunAsync($"SQID-{evt.Id}");

        r.IsSuccess.Should().BeTrue();
        r.Value.Status.Should().Be(nameof(RecalculationRunStatus.Completed));
        r.Value.Mode.Should().Be(nameof(RecalculationMode.DryRun));
        r.Value.TotalSkipped.Should().BeGreaterThan(0);
        var rows = await db.RecalculationDecisionResults.ToListAsync();
        rows.Should().NotBeEmpty();
        rows.Should().AllSatisfy(row =>
        {
            row.Status.Should().Be(RecalculationResultStatus.Skipped);
            row.Reason.Should().Be(ErrorCodes.NoStrategyRegistered);
        });
    }

    [Fact]
    public async Task DryRun_WithFakeStrategy_RecordsComputedRowsWithCorrectDelta()
    {
        using var db = RecalculationTestHelpers.CreateContext();
        var evt = await RecalculationTestHelpers.SeedReadyEventAsync(db);
        var fake = new RecalculationTestHelpers.FakeBenefitRecalculationStrategy(
            benefitType: "OldAgePension",
            decisionIds: ThreeDecisionIds,
            oldAmount: 3000m,
            newAmount: 3200m);
        var svc = RecalculationTestHelpers.NewMassRecalcService(
            db,
            RecalculationTestHelpers.NewAuditCapturing(out _),
            strategies: new IBenefitRecalculationStrategy[] { fake });

        var r = await svc.StartDryRunAsync($"SQID-{evt.Id}");

        r.IsSuccess.Should().BeTrue();
        r.Value.TotalDecisionsRecalculated.Should().Be(3);
        r.Value.TotalDeltaMdl.Should().Be(600m); // (3200-3000) * 3
        var rows = await db.RecalculationDecisionResults.ToListAsync();
        rows.Should().HaveCount(3);
        rows.Should().AllSatisfy(row =>
        {
            row.Status.Should().Be(RecalculationResultStatus.Computed);
            row.OldAmountMdl.Should().Be(3000m);
            row.NewAmountMdl.Should().Be(3200m);
            row.DeltaMdl.Should().Be(200m);
        });
    }

    [Fact]
    public async Task DryRun_WhenPeakHourGateBlocks_ReturnsConflictPeakHourBlocked()
    {
        using var db = RecalculationTestHelpers.CreateContext();
        var evt = await RecalculationTestHelpers.SeedReadyEventAsync(db);
        var svc = RecalculationTestHelpers.NewMassRecalcService(
            db,
            RecalculationTestHelpers.NewAuditCapturing(out _),
            strategies: NoStrategies,
            gateAllows: false);

        var r = await svc.StartDryRunAsync($"SQID-{evt.Id}");

        r.IsSuccess.Should().BeFalse();
        r.ErrorCode.Should().Be(ErrorCodes.Conflict);
        r.ErrorMessage.Should().Be(ErrorCodes.PeakHourBlocked);
    }

    [Fact]
    public async Task ApplyApproved_AfterDryRun_CallsStrategyApplyAndStampsApplied()
    {
        using var db = RecalculationTestHelpers.CreateContext();
        var evt = await RecalculationTestHelpers.SeedReadyEventAsync(db);
        var fake = new RecalculationTestHelpers.FakeBenefitRecalculationStrategy(
            "OldAgePension", TwoDecisionIdsApply);
        var svc = RecalculationTestHelpers.NewMassRecalcService(
            db,
            RecalculationTestHelpers.NewAuditCapturing(out _),
            strategies: new IBenefitRecalculationStrategy[] { fake });

        var dry = await svc.StartDryRunAsync($"SQID-{evt.Id}");
        dry.IsSuccess.Should().BeTrue();

        var applied = await svc.ApplyApprovedResultsAsync(dry.Value.Id);

        applied.IsSuccess.Should().BeTrue();
        fake.AppliedDecisionIds.Should().BeEquivalentTo(TwoDecisionIdsApply);
        var rows = await db.RecalculationDecisionResults.ToListAsync();
        rows.Should().AllSatisfy(row =>
        {
            row.Status.Should().Be(RecalculationResultStatus.Applied);
            row.AppliedAt.Should().NotBeNull();
        });
        var refreshed = await db.LegalChangeEvents.SingleAsync();
        refreshed.Status.Should().Be(LegalChangeEventStatus.Applied);
    }

    [Fact]
    public async Task RejectResult_MarksRowRejected_AndApplyApprovedSkipsIt()
    {
        using var db = RecalculationTestHelpers.CreateContext();
        var evt = await RecalculationTestHelpers.SeedReadyEventAsync(db);
        var fake = new RecalculationTestHelpers.FakeBenefitRecalculationStrategy(
            "OldAgePension", TwoDecisionIdsReject);
        var svc = RecalculationTestHelpers.NewMassRecalcService(
            db,
            RecalculationTestHelpers.NewAuditCapturing(out _),
            strategies: new IBenefitRecalculationStrategy[] { fake });

        var dry = await svc.StartDryRunAsync($"SQID-{evt.Id}");
        dry.IsSuccess.Should().BeTrue();
        var firstRow = await db.RecalculationDecisionResults.FirstAsync();

        var rej = await svc.RejectResultAsync(
            $"SQID-{firstRow.Id}",
            new RecalculationResultRejectInputDto("Operator excludes."));
        rej.IsSuccess.Should().BeTrue();
        rej.Value.Status.Should().Be(nameof(RecalculationResultStatus.Rejected));

        await svc.ApplyApprovedResultsAsync(dry.Value.Id);

        fake.AppliedDecisionIds.Should().NotContain(firstRow.BenefitDecisionId);
    }

    [Fact]
    public async Task RejectResult_OnAlreadyAppliedRow_ReturnsConflict()
    {
        using var db = RecalculationTestHelpers.CreateContext();
        var evt = await RecalculationTestHelpers.SeedReadyEventAsync(db);
        var fake = new RecalculationTestHelpers.FakeBenefitRecalculationStrategy(
            "OldAgePension", OneDecisionIdApplied);
        var svc = RecalculationTestHelpers.NewMassRecalcService(
            db,
            RecalculationTestHelpers.NewAuditCapturing(out _),
            strategies: new IBenefitRecalculationStrategy[] { fake });

        var dry = await svc.StartDryRunAsync($"SQID-{evt.Id}");
        dry.IsSuccess.Should().BeTrue();
        await svc.ApplyApprovedResultsAsync(dry.Value.Id);
        var row = await db.RecalculationDecisionResults.FirstAsync();

        var rej = await svc.RejectResultAsync(
            $"SQID-{row.Id}",
            new RecalculationResultRejectInputDto("Too late."));

        rej.IsSuccess.Should().BeFalse();
        rej.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }
}
