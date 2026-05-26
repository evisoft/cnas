using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Penalties;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0819 / TOR BP 1.2-J — service-level tests for
/// <see cref="LatePaymentPenaltyCalculator"/>. Exercises the no-late /
/// late-with-rate arithmetic, missing-monthly-calc rejection, idempotent
/// upsert behaviour, waive lifecycle, and the
/// <c>LATE_PENALTY.CALCULATED</c> / <c>LATE_PENALTY.WAIVED</c> audit emissions.
/// </summary>
public sealed class LatePaymentPenaltyCalculatorTests
{
    /// <summary>Fixed UTC clock used across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Canonical first-of-month anchor (April 2026).</summary>
    private static readonly DateOnly Month = new(2026, 4, 1);

    /// <summary>Statutory due date for April 2026 with day-25 default = 2026-05-25.</summary>
    private static readonly DateOnly DueDate = new(2026, 5, 25);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-late-penalty-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Stub clock.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Sqid mock — encodes "SQID-{id}".</summary>
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        return sqids;
    }

    /// <summary>Audit capture — exposes the most-recent invocation arguments.</summary>
    private static (IAuditService Audit, Func<(string Code, AuditSeverity Severity)?> Last)
        NewAuditCapture()
    {
        (string Code, AuditSeverity Severity)? slot = null;
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(),
                Arg.Any<AuditSeverity>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<long?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                slot = (call.ArgAt<string>(0), call.ArgAt<AuditSeverity>(1));
                return Task.FromResult(Result.Success());
            });
        return (audit, () => slot);
    }

    /// <summary>Caller stub.</summary>
    private static ICallerContext NewCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(1L);
        caller.UserSqid.Returns("USR-1");
        caller.SourceIp.Returns("127.0.0.1");
        caller.CorrelationId.Returns("corr-penalty");
        caller.Roles.Returns((IReadOnlyCollection<string>)["cnas-admin"]);
        return caller;
    }

    /// <summary>Seeds an active contributor and returns its bigint id.</summary>
    private static async Task<long> SeedContributorAsync(CnasDbContext db)
    {
        var c = new Contributor
        {
            Idno = "1003600012346",
            IdnoHash = IdHashHelper.Hash("1003600012346"),
            Denumire = "SRL Test",
            CreatedAtUtc = ClockNow.AddDays(-30),
            RegisteredAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        db.Contributors.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    /// <summary>Seeds a monthly contribution-calculation row with the supplied totals.</summary>
    private static async Task SeedMonthlyAsync(
        CnasDbContext db,
        long contributorId,
        DateOnly month,
        decimal totalAdjusted)
    {
        db.MonthlyContributionCalculations.Add(new MonthlyContributionCalculation
        {
            ContributorId = contributorId,
            Month = month,
            TotalDeclared = totalAdjusted,
            TotalAdjusted = totalAdjusted,
            DeclarationCount = 1,
            CalculatedAtUtc = ClockNow.AddDays(-1),
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Builds the SUT with the standard PenaltyOptions defaults.</summary>
    private static LatePaymentPenaltyCalculator NewCalculator(CnasDbContext db, IAuditService audit)
    {
        var options = Options.Create(new PenaltyOptions
        {
            DailyRatePercent = 0.03m,
            DueDateOfMonthFollowing = 25,
        });
        return new LatePaymentPenaltyCalculator(
            db,
            db,
            new StubClock(ClockNow),
            NewSqidMock(),
            NewCaller(),
            audit,
            options,
            new LatePaymentPenaltyCalculateInputDtoValidator(),
            new LatePaymentPenaltyWaiveInputDtoValidator());
    }

    /// <summary>R0819 — no-late case: UpToDate ≤ DueDate → DaysLate=0, PenaltyAmount=0.</summary>
    [Fact]
    public async Task CalculateAsync_UpToDateOnOrBeforeDueDate_ZeroPenalty()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        await SeedMonthlyAsync(db, contributorId, Month, totalAdjusted: 1000m);
        var (audit, _) = NewAuditCapture();
        var sut = NewCalculator(db, audit);

        var result = await sut.CalculateAsync(contributorId, Month, DueDate, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DaysLate.Should().Be(0);
        result.Value.PenaltyAmount.Should().Be(0m);
        result.Value.DueDate.Should().Be(DueDate);
    }

    /// <summary>R0819 — happy path: 10 days late × 1000 principal × 0.03% = 3.00 MDL.</summary>
    [Fact]
    public async Task CalculateAsync_TenDaysLate_ComputesExpectedPenalty()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        await SeedMonthlyAsync(db, contributorId, Month, totalAdjusted: 1000m);
        var (audit, _) = NewAuditCapture();
        var sut = NewCalculator(db, audit);

        var upTo = DueDate.AddDays(10);
        var result = await sut.CalculateAsync(contributorId, Month, upTo, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DaysLate.Should().Be(10);
        result.Value.PenaltyAmount.Should().Be(3.00m);
        result.Value.PrincipalAmount.Should().Be(1000m);
        result.Value.DailyRatePercent.Should().Be(0.03m);
    }

    /// <summary>R0819 — missing monthly roll-up → NotFound / MONTHLY_CALC_NOT_FOUND.</summary>
    [Fact]
    public async Task CalculateAsync_NoMonthlyCalc_ReturnsNotFound()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        // intentionally no monthly calc seeded
        var (audit, _) = NewAuditCapture();
        var sut = NewCalculator(db, audit);

        var result = await sut.CalculateAsync(contributorId, Month, DueDate.AddDays(5), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        result.ErrorMessage.Should().Be(LatePaymentPenaltyCalculator.MonthlyCalcNotFoundMessage);
    }

    /// <summary>R0819 — re-run for same (contributor, month, upToDate) upserts in place.</summary>
    [Fact]
    public async Task CalculateAsync_RerunUpsertsInPlace()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        await SeedMonthlyAsync(db, contributorId, Month, totalAdjusted: 500m);
        var (audit, _) = NewAuditCapture();
        var sut = NewCalculator(db, audit);

        var upTo = DueDate.AddDays(20);
        var first = await sut.CalculateAsync(contributorId, Month, upTo, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        var second = await sut.CalculateAsync(contributorId, Month, upTo, CancellationToken.None);
        second.IsSuccess.Should().BeTrue();

        var rows = await db.LatePaymentPenalties
            .Where(r => r.ContributorId == contributorId && r.Month == Month && r.UpToDate == upTo)
            .ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].DaysLate.Should().Be(20);
        second.Value.Id.Should().Be(first.Value.Id);
    }

    /// <summary>R0819 — emits Information audit on the happy path.</summary>
    [Fact]
    public async Task CalculateAsync_AuditsCalculatedEvent()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        await SeedMonthlyAsync(db, contributorId, Month, totalAdjusted: 100m);
        var (audit, last) = NewAuditCapture();
        var sut = NewCalculator(db, audit);

        await sut.CalculateAsync(contributorId, Month, DueDate.AddDays(1), CancellationToken.None);

        last()!.Value.Code.Should().Be(LatePaymentPenaltyCalculator.AuditCalculated);
        last()!.Value.Severity.Should().Be(AuditSeverity.Information);
    }

    /// <summary>R0819 — Waive sets IsWaived + emits Critical audit.</summary>
    [Fact]
    public async Task WaiveAsync_HappyPath_FlipsIsWaivedAndAuditsCritical()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        await SeedMonthlyAsync(db, contributorId, Month, totalAdjusted: 100m);
        var (audit, last) = NewAuditCapture();
        var sut = NewCalculator(db, audit);

        var calc = await sut.CalculateAsync(contributorId, Month, DueDate.AddDays(3), CancellationToken.None);
        calc.IsSuccess.Should().BeTrue();
        var entity = await db.LatePaymentPenalties.SingleAsync();

        var result = await sut.WaiveAsync(entity.Id, "Court-ordered remission", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await db.LatePaymentPenalties.SingleAsync();
        reloaded.IsWaived.Should().BeTrue();
        reloaded.WaiveReason.Should().Be("Court-ordered remission");
        last()!.Value.Code.Should().Be(LatePaymentPenaltyCalculator.AuditWaived);
        last()!.Value.Severity.Should().Be(AuditSeverity.Critical);
    }

    /// <summary>R0819 — second WaiveAsync against an already-waived row returns Conflict.</summary>
    [Fact]
    public async Task WaiveAsync_AlreadyWaived_ReturnsConflict()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        await SeedMonthlyAsync(db, contributorId, Month, totalAdjusted: 100m);
        var (audit, _) = NewAuditCapture();
        var sut = NewCalculator(db, audit);

        await sut.CalculateAsync(contributorId, Month, DueDate.AddDays(3), CancellationToken.None);
        var entity = await db.LatePaymentPenalties.SingleAsync();
        await sut.WaiveAsync(entity.Id, "First waive", CancellationToken.None);

        var second = await sut.WaiveAsync(entity.Id, "Second waive attempt", CancellationToken.None);

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }
}
