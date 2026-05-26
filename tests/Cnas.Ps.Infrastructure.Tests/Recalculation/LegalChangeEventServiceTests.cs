using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Recalculation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Tests.Recalculation;

/// <summary>
/// R1503 / TOR §3.7-D — service-level tests for
/// <see cref="LegalChangeEventService"/>. Exercises the register / modify /
/// mark-ready / cancel flow, the auto-generated code path, the audit
/// emission, and the conflict guards.
/// </summary>
public sealed class LegalChangeEventServiceTests
{
    /// <summary>Static-readonly benefit-types list reused across multiple register inputs.</summary>
    private static readonly string[] DefaultBenefitTypes = { "OldAgePension", "DisabilityPension" };

    /// <summary>Static-readonly empty list used by the Scope=All path so the helper does not allocate per-call.</summary>
    private static readonly string[] EmptyBenefitTypes = Array.Empty<string>();

    private static LegalChangeEventRegisterInputDto NewRegisterInput(string? code = null) => new(
        Code: code,
        Title: "Pension floor raise 2026-07",
        Description: "From 3000 to 3200 MDL",
        EffectiveFrom: new DateOnly(2026, 7, 1),
        Scope: nameof(LegalChangeScope.Pension),
        BenefitTypesInScope: DefaultBenefitTypes,
        ChangePayloadJson: "{\"minimumPensionMdl\":3200.00}");

    [Fact]
    public async Task RegisterAsync_HappyPath_PersistsAndEmitsCriticalAudit()
    {
        using var db = RecalculationTestHelpers.CreateContext();
        var audit = RecalculationTestHelpers.NewAuditCapturing(out var codes);
        var svc = RecalculationTestHelpers.NewLegalChangeEventService(db, audit);

        var result = await svc.RegisterAsync(NewRegisterInput(code: "MIN_PENSION_2026_07"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().Be("MIN_PENSION_2026_07");
        result.Value.Status.Should().Be(nameof(LegalChangeEventStatus.Draft));
        var stored = await db.LegalChangeEvents.SingleAsync();
        stored.Title.Should().Be("Pension floor raise 2026-07");
        codes.Should().Contain(LegalChangeEventService.AuditRegistered);
    }

    [Fact]
    public async Task RegisterAsync_NoCodeSupplied_AutoGeneratesLcePrefix()
    {
        using var db = RecalculationTestHelpers.CreateContext();
        var svc = RecalculationTestHelpers.NewLegalChangeEventService(
            db, RecalculationTestHelpers.NewAuditCapturing(out _));

        var r = await svc.RegisterAsync(NewRegisterInput(code: null));

        r.IsSuccess.Should().BeTrue();
        r.Value.Code.Should().StartWith("LCE-2026-");
    }

    [Fact]
    public async Task RegisterAsync_ScopeAll_SnapshotsEveryBenefitType()
    {
        using var db = RecalculationTestHelpers.CreateContext();
        var svc = RecalculationTestHelpers.NewLegalChangeEventService(
            db, RecalculationTestHelpers.NewAuditCapturing(out _));

        var input = NewRegisterInput(code: "ALL_BUMP_2026") with
        {
            Scope = nameof(LegalChangeScope.All),
            BenefitTypesInScope = EmptyBenefitTypes,
        };
        var r = await svc.RegisterAsync(input);

        r.IsSuccess.Should().BeTrue();
        r.Value.BenefitTypesInScope.Should()
            .Contain(Enum.GetNames<BenefitType>());
    }

    [Fact]
    public async Task ModifyAsync_OnReadyRow_ReturnsConflict()
    {
        using var db = RecalculationTestHelpers.CreateContext();
        var evt = await RecalculationTestHelpers.SeedReadyEventAsync(db);
        var svc = RecalculationTestHelpers.NewLegalChangeEventService(
            db, RecalculationTestHelpers.NewAuditCapturing(out _));

        var r = await svc.ModifyAsync(
            $"SQID-{evt.Id}",
            new LegalChangeEventModifyInputDto(
                Title: "new title",
                Description: null, EffectiveFrom: null, Scope: null,
                BenefitTypesInScope: null, ChangePayloadJson: null,
                ChangeReason: "fixing typo"));

        r.IsSuccess.Should().BeFalse();
        r.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    [Fact]
    public async Task MarkReadyAsync_FromDraft_FlipsToReadyAndAudits()
    {
        using var db = RecalculationTestHelpers.CreateContext();
        var evt = await RecalculationTestHelpers.SeedReadyEventAsync(
            db, status: LegalChangeEventStatus.Draft);
        var audit = RecalculationTestHelpers.NewAuditCapturing(out var codes);
        var svc = RecalculationTestHelpers.NewLegalChangeEventService(db, audit);

        var r = await svc.MarkReadyAsync($"SQID-{evt.Id}");

        r.IsSuccess.Should().BeTrue();
        r.Value.Status.Should().Be(nameof(LegalChangeEventStatus.Ready));
        codes.Should().Contain(LegalChangeEventService.AuditMarkedReady);
    }

    [Fact]
    public async Task CancelAsync_PopulatesReasonAndEmitsCriticalAudit()
    {
        using var db = RecalculationTestHelpers.CreateContext();
        var evt = await RecalculationTestHelpers.SeedReadyEventAsync(db);
        var audit = RecalculationTestHelpers.NewAuditCapturing(out var codes);
        var svc = RecalculationTestHelpers.NewLegalChangeEventService(db, audit);

        var r = await svc.CancelAsync(
            $"SQID-{evt.Id}",
            new LegalChangeEventReasonInputDto("Decree retracted on 2026-06-30."));

        r.IsSuccess.Should().BeTrue();
        r.Value.Status.Should().Be(nameof(LegalChangeEventStatus.Cancelled));
        r.Value.CancellationReason.Should().Be("Decree retracted on 2026-06-30.");
        codes.Should().Contain(LegalChangeEventService.AuditCancelled);
    }
}
