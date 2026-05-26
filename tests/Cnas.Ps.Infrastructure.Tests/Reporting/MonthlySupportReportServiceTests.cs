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
/// R2461 / Deliverable 7.1 — service-level tests for
/// <see cref="MonthlySupportReportService"/>. Exercises the empty-month
/// case, the basic aggregation, SLA breach rates, and the
/// CategoryCodes filter.
/// </summary>
public sealed class MonthlySupportReportServiceTests
{
    /// <summary>Fixed clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>CA1861 — hoisted to a static field to avoid per-call allocation.</summary>
    private static readonly string[] PaymentOnly = ["PAYMENT"];

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
            .UseInMemoryDatabase($"cnas-monthly-support-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Builds the SUT against the supplied context.</summary>
    private static MonthlySupportReportService NewService(CnasDbContext db)
        => new(
            db: db,
            clock: new StubClock(ClockNow),
            validator: new MonthlySupportReportInputValidator(new StubClock(ClockNow)));

    /// <summary>Seeds a category and returns it.</summary>
    private static async Task<SupportTicketCategory> SeedCategoryAsync(
        CnasDbContext db,
        string code = "AUTH",
        int firstResponseMinutes = 60,
        int resolutionMinutes = 480)
    {
        var cat = new SupportTicketCategory
        {
            Code = code,
            DisplayName = $"Test {code}",
            DefaultSeverity = SupportTicketSeverity.Normal,
            FirstResponseSlaMinutes = firstResponseMinutes,
            ResolutionSlaMinutes = resolutionMinutes,
            EscalationQueueCode = "L2_GENERAL",
            RegisteredByUserId = 1,
            CreatedAtUtc = ClockNow,
            CreatedBy = "SQID-1",
            IsActive = true,
        };
        db.SupportTicketCategories.Add(cat);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return cat;
    }

    /// <summary>Seeds a support ticket and returns it.</summary>
    private static async Task<SupportTicket> SeedTicketAsync(
        CnasDbContext db,
        long categoryId,
        DateTime submittedAt,
        SupportTicketStatus status = SupportTicketStatus.Submitted,
        SupportTicketSeverity severity = SupportTicketSeverity.Normal,
        DateTime? firstAcknowledgedAt = null,
        DateTime? resolvedAt = null,
        DateTime? closedAt = null,
        DateTime? escalatedAt = null)
    {
        var ticket = new SupportTicket
        {
            TicketNumber = $"TKT-2026-{Guid.NewGuid().ToString("N")[..6]}",
            CategoryId = categoryId,
            Title = "test",
            Description = "body",
            Severity = severity,
            Status = status,
            SubmittedByUserId = 1,
            SubmittedAt = submittedAt,
            FirstAcknowledgedAt = firstAcknowledgedAt,
            ResolvedAt = resolvedAt,
            ClosedAt = closedAt,
            EscalatedAt = escalatedAt,
            FirstResponseDueAt = submittedAt.AddMinutes(60),
            ResolutionDueAt = submittedAt.AddMinutes(480),
            CreatedAtUtc = submittedAt,
            CreatedBy = "SQID-1",
            IsActive = true,
        };
        db.SupportTickets.Add(ticket);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return ticket;
    }

    /// <summary>Seeds an SLA event row.</summary>
    private static async Task SeedSlaEventAsync(
        CnasDbContext db,
        long ticketId,
        SupportTicketSlaEventKind kind,
        DateTime detectedAt)
    {
        db.SupportTicketSlaEvents.Add(new SupportTicketSlaEvent
        {
            TicketId = ticketId,
            EventKind = kind,
            DetectedAt = detectedAt,
            Notes = kind.ToString(),
            CreatedAtUtc = detectedAt,
            CreatedBy = "SYSTEM",
            IsActive = true,
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>R2461 — empty month returns zero totals.</summary>
    [Fact]
    public async Task ComputeAsync_EmptyMonth_ReturnsZeroTotals()
    {
        using var db = CreateContext();
        await SeedCategoryAsync(db);
        var sut = NewService(db);

        var result = await sut.ComputeAsync(
            new MonthlySupportReportInputDto(new DateOnly(2026, 4, 1), null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalSubmitted.Should().Be(0);
        result.Value.TotalResolved.Should().Be(0);
        result.Value.FirstResponseBreachRate.Should().Be(0m);
        result.Value.ResolutionBreachRate.Should().Be(0m);
        result.Value.SeverityBreakdown.Should().BeEmpty();
        result.Value.CategoryBreakdown.Should().BeEmpty();
        result.Value.Month.Should().Be(new DateOnly(2026, 4, 1));
        result.Value.GeneratedAtUtc.Should().Be(ClockNow);
    }

    /// <summary>R2461 — tickets in the requested month are counted.</summary>
    [Fact]
    public async Task ComputeAsync_TicketsInMonth_AggregatesCorrectly()
    {
        using var db = CreateContext();
        var cat = await SeedCategoryAsync(db);

        // Two submitted in April; one resolved within April; one closed.
        var aprilStart = new DateTime(2026, 4, 10, 9, 0, 0, DateTimeKind.Utc);
        await SeedTicketAsync(db, cat.Id, aprilStart, status: SupportTicketStatus.Resolved,
            firstAcknowledgedAt: aprilStart.AddMinutes(30),
            resolvedAt: aprilStart.AddHours(3));
        await SeedTicketAsync(db, cat.Id, aprilStart.AddDays(1), status: SupportTicketStatus.Closed,
            firstAcknowledgedAt: aprilStart.AddDays(1).AddMinutes(15),
            resolvedAt: aprilStart.AddDays(1).AddHours(2),
            closedAt: aprilStart.AddDays(1).AddHours(5));

        // One submitted in March — must NOT be counted.
        await SeedTicketAsync(db, cat.Id, new DateTime(2026, 3, 20, 10, 0, 0, DateTimeKind.Utc));

        var sut = NewService(db);

        var result = await sut.ComputeAsync(
            new MonthlySupportReportInputDto(new DateOnly(2026, 4, 1), null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalSubmitted.Should().Be(2);
        result.Value.TotalResolved.Should().Be(2);
        result.Value.TotalClosed.Should().Be(1);
        result.Value.AvgFirstResponseMinutes.Should().NotBeNull();
        result.Value.AvgResolutionMinutes.Should().NotBeNull();
        result.Value.CategoryBreakdown.Should().HaveCount(1);
        result.Value.CategoryBreakdown[0].CategoryCode.Should().Be("AUTH");
        result.Value.CategoryBreakdown[0].TotalSubmitted.Should().Be(2);
    }

    /// <summary>R2461 — SLA breach events drive the breach rate fields.</summary>
    [Fact]
    public async Task ComputeAsync_SlaBreaches_ProduceBreachRates()
    {
        using var db = CreateContext();
        var cat = await SeedCategoryAsync(db);

        var aprilStart = new DateTime(2026, 4, 5, 8, 0, 0, DateTimeKind.Utc);
        var t1 = await SeedTicketAsync(db, cat.Id, aprilStart);
        var t2 = await SeedTicketAsync(db, cat.Id, aprilStart.AddDays(2));

        // 1 of 2 tickets has a first-response breach event in the month.
        await SeedSlaEventAsync(db, t1.Id, SupportTicketSlaEventKind.FirstResponseBreached, aprilStart.AddHours(2));
        // Both have resolution breach events.
        await SeedSlaEventAsync(db, t1.Id, SupportTicketSlaEventKind.ResolutionBreached, aprilStart.AddHours(10));
        await SeedSlaEventAsync(db, t2.Id, SupportTicketSlaEventKind.ResolutionBreached, aprilStart.AddDays(2).AddHours(10));

        var sut = NewService(db);

        var result = await sut.ComputeAsync(
            new MonthlySupportReportInputDto(new DateOnly(2026, 4, 1), null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FirstResponseBreachRate.Should().Be(0.5m);
        result.Value.ResolutionBreachRate.Should().Be(1m);
        result.Value.CategoryBreakdown[0].FirstResponseBreachRate.Should().Be(0.5m);
        result.Value.CategoryBreakdown[0].ResolutionBreachRate.Should().Be(1m);
    }

    /// <summary>R2461 — CategoryCodes filter limits which tickets are aggregated.</summary>
    [Fact]
    public async Task ComputeAsync_CategoryCodesFilter_LimitsResults()
    {
        using var db = CreateContext();
        var auth = await SeedCategoryAsync(db, code: "AUTH");
        var payment = await SeedCategoryAsync(db, code: "PAYMENT");

        var aprilStart = new DateTime(2026, 4, 5, 8, 0, 0, DateTimeKind.Utc);
        await SeedTicketAsync(db, auth.Id, aprilStart);
        await SeedTicketAsync(db, payment.Id, aprilStart.AddHours(1));
        await SeedTicketAsync(db, payment.Id, aprilStart.AddHours(2));

        var sut = NewService(db);

        var result = await sut.ComputeAsync(
            new MonthlySupportReportInputDto(
                new DateOnly(2026, 4, 1),
                CategoryCodes: PaymentOnly),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalSubmitted.Should().Be(2);
        result.Value.CategoryBreakdown.Should().HaveCount(1);
        result.Value.CategoryBreakdown.Single().CategoryCode.Should().Be("PAYMENT");
    }

    /// <summary>R2461 — invalid input is rejected with VALIDATION_FAILED.</summary>
    [Fact]
    public async Task ComputeAsync_InvalidInput_ReturnsValidationFailure()
    {
        using var db = CreateContext();
        var sut = NewService(db);

        var result = await sut.ComputeAsync(
            new MonthlySupportReportInputDto(new DateOnly(2026, 4, 15), null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>R2461 — severity breakdown groups tickets by their current severity.</summary>
    [Fact]
    public async Task ComputeAsync_GroupsBySeverity()
    {
        using var db = CreateContext();
        var cat = await SeedCategoryAsync(db);

        var aprilStart = new DateTime(2026, 4, 5, 8, 0, 0, DateTimeKind.Utc);
        await SeedTicketAsync(db, cat.Id, aprilStart, severity: SupportTicketSeverity.Normal);
        await SeedTicketAsync(db, cat.Id, aprilStart.AddHours(1), severity: SupportTicketSeverity.High);
        await SeedTicketAsync(db, cat.Id, aprilStart.AddHours(2), severity: SupportTicketSeverity.High);

        var sut = NewService(db);

        var result = await sut.ComputeAsync(
            new MonthlySupportReportInputDto(new DateOnly(2026, 4, 1), null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.SeverityBreakdown.Should().HaveCount(2);
        result.Value.SeverityBreakdown.Should()
            .Contain(r => r.Severity == "Normal" && r.TotalSubmitted == 1);
        result.Value.SeverityBreakdown.Should()
            .Contain(r => r.Severity == "High" && r.TotalSubmitted == 2);
    }
}
