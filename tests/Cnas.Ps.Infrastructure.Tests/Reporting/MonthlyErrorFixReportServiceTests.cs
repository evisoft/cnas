using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Reporting;

/// <summary>
/// R2462 / Deliverable 7.2 — service-level tests for
/// <see cref="MonthlyErrorFixReportService"/>. Exercises empty-month
/// behaviour, integrity-finding bucketing, change-request counts, and
/// template-variant update counts.
/// </summary>
public sealed class MonthlyErrorFixReportServiceTests
{
    /// <summary>Fixed clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Stub clock returning the fixed instant.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Builds a fresh EF Core InMemory context for one test.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-monthly-errorfix-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Builds the SUT against the supplied context.</summary>
    private static MonthlyErrorFixReportService NewService(CnasDbContext db)
        => new(
            db: db,
            clock: new StubClock(ClockNow),
            validator: new MonthlyErrorFixReportInputValidator(new StubClock(ClockNow)));

    /// <summary>Seeds an integrity finding row.</summary>
    private static async Task SeedFindingAsync(
        CnasDbContext db,
        string aggregateName,
        IntegrityFindingSeverity severity,
        DateTime firstDetectedAt,
        int runId = 1)
    {
        db.IntegrityCheckFindings.Add(new IntegrityCheckFinding
        {
            RunId = runId,
            CheckCode = "INV.TEST",
            Severity = severity,
            AggregateName = aggregateName,
            AggregateRowId = 1,
            Description = "test invariant violation",
            FirstDetectedAt = firstDetectedAt,
            CreatedAtUtc = firstDetectedAt,
            CreatedBy = "SYSTEM",
            IsActive = true,
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Seeds a change request row.</summary>
    private static async Task SeedChangeRequestAsync(
        CnasDbContext db,
        ChangeRequestStatus status,
        DateTime createdAt,
        DateTime? deployedAt = null,
        DateTime? rolledBackAt = null)
    {
        db.ChangeRequests.Add(new ChangeRequest
        {
            ChangeNumber = $"CHG-2026-{Guid.NewGuid().ToString("N")[..6]}",
            Title = "test",
            Description = new string('x', 60),
            Kind = ChangeRequestKind.Normal,
            Status = status,
            Risk = ChangeRequestRisk.Low,
            ImpactedSystems = "API",
            RollbackPlan = new string('y', 60),
            RequestedByUserId = 1,
            DeployedAt = deployedAt,
            RolledBackAt = rolledBackAt,
            CreatedAtUtc = createdAt,
            CreatedBy = "SQID-1",
            IsActive = true,
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Seeds a document template + variant pair.</summary>
    private static async Task<TemplateVariant> SeedTemplateVariantAsync(
        CnasDbContext db,
        string language,
        DateTime createdAt,
        DateTime? updatedAt = null)
    {
        // Each test gets a freshly minted template to avoid (TemplateId, Language)
        // uniqueness collisions on the variant configuration.
        var template = new DocumentTemplate
        {
            Code = $"tpl-{Guid.NewGuid():N}",
            Name = "test template",
            Version = 1,
            IsCurrent = true,
            StorageObjectKey = $"templates/tpl/v1/blob.docx",
            ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ContentLength = 100,
            ContentSha256 = new string('a', 64),
            DefaultLanguage = "ro",
            CreatedAtUtc = createdAt,
            CreatedBy = "SQID-1",
            IsActive = true,
        };
        db.DocumentTemplates.Add(template);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var variant = new TemplateVariant
        {
            TemplateId = template.Id,
            Language = language,
            SubjectOrTitle = "subj",
            Body = "body",
            IsApproved = false,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = updatedAt,
            CreatedBy = "SQID-1",
            IsActive = true,
        };
        db.TemplateVariants.Add(variant);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return variant;
    }

    /// <summary>R2462 — empty month returns zero totals.</summary>
    [Fact]
    public async Task ComputeAsync_EmptyMonth_ReturnsZeroTotals()
    {
        using var db = CreateContext();
        var sut = NewService(db);

        var result = await sut.ComputeAsync(
            new MonthlyErrorFixReportInputDto(new DateOnly(2026, 4, 1)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalIntegrityFindings.Should().Be(0);
        result.Value.TotalChangeRequestsRolledBack.Should().Be(0);
        result.Value.TotalChangeRequestsDeployed.Should().Be(0);
        result.Value.TotalDocumentationTemplatesUpdated.Should().Be(0);
        result.Value.CategoryBreakdown.Should().BeEmpty();
        result.Value.Month.Should().Be(new DateOnly(2026, 4, 1));
        result.Value.GeneratedAtUtc.Should().Be(ClockNow);
    }

    /// <summary>R2462 — integrity findings are bucketed by severity.</summary>
    [Fact]
    public async Task ComputeAsync_IntegrityFindings_CountedBySeverity()
    {
        using var db = CreateContext();
        var aprilStart = new DateTime(2026, 4, 10, 9, 0, 0, DateTimeKind.Utc);
        await SeedFindingAsync(db, "Claim", IntegrityFindingSeverity.Critical, aprilStart);
        await SeedFindingAsync(db, "Claim", IntegrityFindingSeverity.High, aprilStart.AddDays(1));
        await SeedFindingAsync(db, "ExecutoryDocument", IntegrityFindingSeverity.High, aprilStart.AddDays(2));
        await SeedFindingAsync(db, "UserProfile", IntegrityFindingSeverity.Low, aprilStart.AddDays(3));

        // Out-of-month finding — must NOT count.
        await SeedFindingAsync(db, "Claim", IntegrityFindingSeverity.Critical, new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc));

        var sut = NewService(db);

        var result = await sut.ComputeAsync(
            new MonthlyErrorFixReportInputDto(new DateOnly(2026, 4, 1)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalIntegrityFindings.Should().Be(4);
        result.Value.IntegrityFindingsByCriticalSeverity.Should().Be(1);
        result.Value.IntegrityFindingsByHighSeverity.Should().Be(2);
        result.Value.IntegrityFindingsByMediumSeverity.Should().Be(0);
        result.Value.IntegrityFindingsByLowSeverity.Should().Be(1);
        result.Value.CategoryBreakdown.Should().HaveCount(4);
        result.Value.CategoryBreakdown.Should()
            .Contain(r => r.AggregateName == "Claim" && r.Severity == "Critical" && r.Count == 1);
        result.Value.CategoryBreakdown.Should()
            .Contain(r => r.AggregateName == "ExecutoryDocument" && r.Severity == "High" && r.Count == 1);
    }

    /// <summary>R2462 — change-request deploy / rollback counts pick up the right rows.</summary>
    [Fact]
    public async Task ComputeAsync_ChangeRequestRollbacks_AreCounted()
    {
        using var db = CreateContext();
        var aprilStart = new DateTime(2026, 4, 10, 9, 0, 0, DateTimeKind.Utc);

        await SeedChangeRequestAsync(db, ChangeRequestStatus.RolledBack, aprilStart,
            deployedAt: aprilStart.AddHours(1),
            rolledBackAt: aprilStart.AddHours(3));
        await SeedChangeRequestAsync(db, ChangeRequestStatus.RolledBack, aprilStart.AddDays(2),
            deployedAt: aprilStart.AddDays(2),
            rolledBackAt: aprilStart.AddDays(2).AddHours(5));
        await SeedChangeRequestAsync(db, ChangeRequestStatus.Deployed, aprilStart.AddDays(5),
            deployedAt: aprilStart.AddDays(5).AddHours(1));

        // Out-of-month rollback — must NOT count.
        await SeedChangeRequestAsync(db, ChangeRequestStatus.RolledBack, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            deployedAt: new DateTime(2026, 3, 1, 1, 0, 0, DateTimeKind.Utc),
            rolledBackAt: new DateTime(2026, 3, 1, 5, 0, 0, DateTimeKind.Utc));

        var sut = NewService(db);

        var result = await sut.ComputeAsync(
            new MonthlyErrorFixReportInputDto(new DateOnly(2026, 4, 1)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalChangeRequestsRolledBack.Should().Be(2);
        result.Value.TotalChangeRequestsDeployed.Should().Be(3);
    }

    /// <summary>R2462 — template variant updates land in the bucket.</summary>
    [Fact]
    public async Task ComputeAsync_TemplateVariantUpdates_AreCounted()
    {
        using var db = CreateContext();
        var aprilStart = new DateTime(2026, 4, 10, 9, 0, 0, DateTimeKind.Utc);

        // Created in April with no later update — counts (the row's latest write is the creation).
        await SeedTemplateVariantAsync(db, "ro", aprilStart);
        // Created in March but updated in April — counts.
        await SeedTemplateVariantAsync(db, "en", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            updatedAt: aprilStart.AddDays(5));
        // Created and updated in March — must NOT count.
        await SeedTemplateVariantAsync(db, "ru", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            updatedAt: new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc));

        var sut = NewService(db);

        var result = await sut.ComputeAsync(
            new MonthlyErrorFixReportInputDto(new DateOnly(2026, 4, 1)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalDocumentationTemplatesUpdated.Should().Be(2);
    }

    /// <summary>R2462 — invalid input (mid-month day) is rejected.</summary>
    [Fact]
    public async Task ComputeAsync_InvalidInput_ReturnsValidationFailure()
    {
        using var db = CreateContext();
        var sut = NewService(db);

        var result = await sut.ComputeAsync(
            new MonthlyErrorFixReportInputDto(new DateOnly(2026, 4, 15)),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }
}
