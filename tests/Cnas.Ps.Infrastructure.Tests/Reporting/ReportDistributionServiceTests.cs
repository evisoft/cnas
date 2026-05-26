using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Tests.Reporting;

/// <summary>
/// R1906 / TOR Annex 6 — service-level tests for
/// <see cref="ReportDistributionService"/>. Verifies the create-rule
/// happy-path emits the audit row + the
/// <c>cnas.report_distribution.rule_created</c> counter, the modify-after-
/// delete branch returns NOT_FOUND, the list endpoint honours the
/// ReportCode filter, and the disable transition flips IsActive.
/// </summary>
public sealed class ReportDistributionServiceTests
{
    private static ReportDistributionService NewService(CnasDbContext db, out List<string> auditCodes)
    {
        var audit = ReportDistributionTestHelpers.NewAudit(out auditCodes);
        var caller = ReportDistributionTestHelpers.NewCaller();
        var sqids = ReportDistributionTestHelpers.NewSqidMock();
        var hasher = ReportDistributionTestHelpers.NewHasher();
        return new ReportDistributionService(
            db: db,
            clock: new ReportDistributionTestHelpers.StubClock(ReportDistributionTestHelpers.ClockNow),
            caller: caller,
            sqids: sqids,
            audit: audit,
            hasher: hasher,
            createValidator: new ReportDistributionRuleCreateInputValidator(),
            modifyValidator: new ReportDistributionRuleModifyInputValidator(),
            reasonValidator: new ReportDistributionReasonInputValidator(),
            ruleFilterValidator: new ReportDistributionRuleFilterValidator(),
            dispatchFilterValidator: new ReportDispatchFilterValidator());
    }

    private static ReportDistributionRuleCreateInputDto MakeCreateInput(
        string reportCode = "ACCESS_RIGHTS.FULL_MATRIX",
        string channel = "Email",
        string recipientKind = "EmailAddress",
        string recipientCode = "ops@example.org",
        string format = "Pdf",
        string priority = "Normal")
        => new(
            ReportCode: reportCode,
            Channel: channel,
            RecipientKind: recipientKind,
            RecipientCode: recipientCode,
            Format: format,
            Priority: priority,
            EffectiveFrom: new DateOnly(2026, 1, 1),
            EffectiveUntil: null,
            Notes: null);

    [Fact]
    public async Task CreateRuleAsync_HappyPath_PersistsAndEmitsAudit()
    {
        using var db = ReportDistributionTestHelpers.CreateContext();
        var svc = NewService(db, out var auditCodes);

        var counterValue = 0L;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == CnasMeter.MeterName
                    && instrument.Name == "cnas.report_distribution.rule_created")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, m, _, _) => Interlocked.Add(ref counterValue, m));
        listener.Start();

        var result = await svc.CreateRuleAsync(MakeCreateInput());

        result.IsSuccess.Should().BeTrue();
        result.Value.ReportCode.Should().Be("ACCESS_RIGHTS.FULL_MATRIX");
        result.Value.IsActive.Should().BeTrue();
        var stored = await db.ReportDistributionRules.SingleAsync();
        stored.RecipientCodeHash.Should().StartWith("HASH:");
        auditCodes.Should().Contain(ReportDistributionService.AuditCodeRuleCreated);
        counterValue.Should().Be(1);
    }

    [Fact]
    public async Task ModifyRuleAsync_AfterDelete_ReturnsNotFound()
    {
        using var db = ReportDistributionTestHelpers.CreateContext();
        var svc = NewService(db, out _);

        var created = await svc.CreateRuleAsync(MakeCreateInput());
        created.IsSuccess.Should().BeTrue();
        var sqid = created.Value.Id;

        var deleted = await svc.DeleteRuleAsync(sqid, new ReportDistributionReasonInputDto("Removing — recipient retired."));
        deleted.IsSuccess.Should().BeTrue();

        var modify = await svc.ModifyRuleAsync(sqid, new ReportDistributionRuleModifyInputDto(
            Channel: null,
            RecipientKind: null,
            RecipientCode: null,
            Format: null,
            Priority: null,
            EffectiveFrom: null,
            EffectiveUntil: null,
            Notes: "follow-up note",
            ChangeReason: "Trying to revive a deleted rule."));

        modify.IsSuccess.Should().BeFalse();
        modify.ErrorCode.Should().Be(Core.Common.ErrorCodes.NotFound);
    }

    [Fact]
    public async Task ListRulesAsync_RespectsReportCodeFilter()
    {
        using var db = ReportDistributionTestHelpers.CreateContext();
        var svc = NewService(db, out _);

        await svc.CreateRuleAsync(MakeCreateInput(reportCode: "ACCESS_RIGHTS.FULL_MATRIX"));
        await svc.CreateRuleAsync(MakeCreateInput(
            reportCode: "INTEGRITY_CHECK.NIGHTLY_SUMMARY",
            recipientCode: "other@example.org"));

        var page = await svc.ListRulesAsync(new ReportDistributionRuleFilterDto(
            ReportCode: "ACCESS_RIGHTS.FULL_MATRIX",
            Channel: null,
            RecipientKind: null,
            IsActive: true,
            Skip: 0,
            Take: 50));

        page.IsSuccess.Should().BeTrue();
        page.Value.Items.Should().HaveCount(1);
        page.Value.Items[0].ReportCode.Should().Be("ACCESS_RIGHTS.FULL_MATRIX");
    }

    [Fact]
    public async Task DisableRuleAsync_FlipsIsActiveAndEmitsAudit()
    {
        using var db = ReportDistributionTestHelpers.CreateContext();
        var svc = NewService(db, out var auditCodes);

        var created = await svc.CreateRuleAsync(MakeCreateInput());
        created.IsSuccess.Should().BeTrue();

        var disabled = await svc.DisableRuleAsync(
            created.Value.Id,
            new ReportDistributionReasonInputDto("Suspending while recipient on leave."));

        disabled.IsSuccess.Should().BeTrue();
        disabled.Value.IsActive.Should().BeFalse();
        auditCodes.Should().Contain(ReportDistributionService.AuditCodeRuleDisabled);
    }
}
