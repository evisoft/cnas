using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ServiceManagement;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.ServiceManagement;

namespace Cnas.Ps.Infrastructure.Tests.ServiceManagement;

/// <summary>
/// R2506 / TOR PIR 037-040 — tests for
/// <see cref="QualityRiskService"/>.
/// </summary>
public sealed class QualityRiskServiceTests
{
    private static QualityRiskService NewService(
        CnasDbContext db,
        IAuditService audit,
        ICallerContext? caller = null,
        DateTime? now = null)
        => new(
            db: db,
            read: db,
            clock: new ServiceManagementTestHelpers.StubClock(now ?? ServiceManagementTestHelpers.ClockNow),
            sqids: ServiceManagementTestHelpers.NewSqidMock(),
            caller: caller ?? ServiceManagementTestHelpers.NewCaller(),
            audit: audit,
            createValidator: new QualityRiskCreateInputValidator(),
            modifyValidator: new QualityRiskModifyInputValidator(),
            reviewValidator: new QualityRiskReviewInputValidator(),
            reasonValidator: new QualityRiskReasonInputValidator(),
            filterValidator: new QualityRiskFilterValidator(),
            actionCreateValidator: new QualityRiskActionCreateInputValidator(),
            actionModifyValidator: new QualityRiskActionModifyInputValidator(),
            actionImplementValidator: new QualityRiskActionImplementInputValidator(),
            actionReasonValidator: new QualityRiskActionReasonInputValidator());

    private static QualityRiskCreateInputDto NewCreateDto(string code = "DATA_LOSS")
        => new(
            RiskCode: code,
            Title: "Risk of payroll data loss during migration",
            Description: "Possible corruption of payroll data during the legacy-to-PostgreSQL migration window.",
            Category: "Technical",
            Likelihood: "Possible",
            Impact: "Major",
            OwnerSqid: "SQID-1");

    [Fact]
    public async Task CreateRisk_HappyPath_Succeeds_AndAudits()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out var codes);
        var svc = NewService(db, audit);

        var result = await svc.CreateRiskAsync(NewCreateDto(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.RiskCode.Should().Be("DATA_LOSS");
        result.Value.Status.Should().Be(nameof(QualityRiskStatus.Open));
        codes.Should().Contain(IQualityRiskService.AuditRiskCreated);
    }

    [Fact]
    public async Task RecordReview_UpdatesLastReviewedAt()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out var codes);
        var svc = NewService(db, audit);

        var created = await svc.CreateRiskAsync(NewCreateDto(), CancellationToken.None);
        created.IsSuccess.Should().BeTrue();
        var sqid = created.Value!.Id;

        var review = await svc.RecordReviewAsync(
            sqid,
            new QualityRiskReviewInputDto("Reviewed; status unchanged but description refined off-band."),
            CancellationToken.None);

        review.IsSuccess.Should().BeTrue();
        review.Value!.LastReviewedAt.Should().NotBeNull();
        review.Value.LastReviewedAt!.Value.Should().Be(ServiceManagementTestHelpers.ClockNow);
        codes.Should().Contain(IQualityRiskService.AuditRiskReviewed);
    }

    [Fact]
    public async Task AddPreventiveAction_TransitionsAndAudits()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out var codes);
        var svc = NewService(db, audit);

        var created = await svc.CreateRiskAsync(NewCreateDto(), CancellationToken.None);
        var sqid = created.Value!.Id;

        var add = await svc.AddPreventiveActionAsync(
            sqid,
            new QualityRiskActionCreateInputDto(
                Description: "Take a verified snapshot before migration starts.",
                DueDate: new DateOnly(2026, 8, 1),
                AssignedToSqid: "SQID-1"),
            CancellationToken.None);

        add.IsSuccess.Should().BeTrue();
        add.Value!.Status.Should().Be(nameof(QualityRiskActionStatus.Planned));
        codes.Should().Contain(IQualityRiskService.AuditActionAdded);

        var inProgress = await svc.MarkActionInProgressAsync(add.Value.Id, CancellationToken.None);
        inProgress.IsSuccess.Should().BeTrue();
        inProgress.Value!.Status.Should().Be(nameof(QualityRiskActionStatus.InProgress));
        codes.Should().Contain(IQualityRiskService.AuditActionInProgress);
    }

    [Fact]
    public async Task ListOverdueForReview_ReturnsNullAndOldReviews()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);
        var svc = NewService(db, audit);

        // Stale — LastReviewedAt is null (never reviewed).
        await svc.CreateRiskAsync(NewCreateDto("NEVER_REVIEWED"), CancellationToken.None);

        // Recently reviewed.
        var fresh = await svc.CreateRiskAsync(NewCreateDto("FRESHLY_REVIEWED"), CancellationToken.None);
        var freshId = fresh.Value!.Id;
        await svc.RecordReviewAsync(freshId, new QualityRiskReviewInputDto("Just reviewed today."), CancellationToken.None);

        // Long-stale — manually seed a row with LastReviewedAt = ClockNow - 400 days.
        var stale = new QualityRisk
        {
            RiskCode = "STALE_REVIEW",
            Title = "Stale-reviewed risk",
            Description = "This risk was last reviewed more than a year ago and should be flagged as overdue.",
            Category = QualityRiskCategory.Process,
            Likelihood = QualityRiskLikelihood.Possible,
            Impact = QualityRiskImpact.Moderate,
            Status = QualityRiskStatus.Open,
            OwnerUserId = 1,
            IdentifiedAt = ServiceManagementTestHelpers.ClockNow.AddDays(-500),
            LastReviewedAt = ServiceManagementTestHelpers.ClockNow.AddDays(-400),
            CreatedAtUtc = ServiceManagementTestHelpers.ClockNow.AddDays(-500),
            CreatedBy = "seed",
            IsActive = true,
        };
        db.QualityRisks.Add(stale);
        await db.SaveChangesAsync();

        var overdue = await svc.ListOverdueForReviewAsync(365, CancellationToken.None);

        overdue.IsSuccess.Should().BeTrue();
        overdue.Value!.Should().HaveCount(2);
        var codes = overdue.Value.Select(r => r.RiskCode).ToList();
        codes.Should().Contain("NEVER_REVIEWED");
        codes.Should().Contain("STALE_REVIEW");
        codes.Should().NotContain("FRESHLY_REVIEWED");
    }
}
