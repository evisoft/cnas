using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Financials;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services.Financials;

/// <summary>
/// R0814 / TOR BP 1.2-E — service-level tests for the BASS refund workflow
/// (<see cref="BassRefundService"/>).
/// </summary>
public sealed class BassRefundServiceTests
{
    /// <summary>Fixed UTC clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Canonical reporting month anchored on the first of the month.</summary>
    private static readonly DateOnly ReportingMonth = new(2026, 4, 1);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-bass-refunds-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Stub clock returning the fixed instant.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Sqid mock that round-trips between "SQID-{id}" strings and bigint ids.</summary>
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            var s = call.Arg<string>();
            if (s is not null && s.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(s["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }

    /// <summary>Captures audit invocations for assertion.</summary>
    private static (IAuditService Audit, Func<List<(string Code, AuditSeverity Severity, long? TargetId)>> Calls)
        NewAuditCapture()
    {
        var calls = new List<(string Code, AuditSeverity Severity, long? TargetId)>();
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
                calls.Add((
                    call.ArgAt<string>(0),
                    call.ArgAt<AuditSeverity>(1),
                    call.ArgAt<long?>(4)));
                return Task.FromResult(Result.Success());
            });
        return (audit, () => calls);
    }

    /// <summary>Authenticated-caller helper.</summary>
    private static ICallerContext NewCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(1L);
        caller.UserSqid.Returns("USR-1");
        caller.SourceIp.Returns("203.0.113.7");
        caller.CorrelationId.Returns("corr-bass");
        caller.Roles.Returns((IReadOnlyCollection<string>)["cnas-admin"]);
        return caller;
    }

    /// <summary>Builds the SUT with all collaborators wired.</summary>
    private static BassRefundService NewService(CnasDbContext db, IAuditService audit)
    {
        var clock = new StubClock(ClockNow);
        return new(
            db,
            clock,
            NewSqidMock(),
            NewCaller(),
            audit,
            new BassRefundRequestInputDtoValidator(),
            new BassRefundIssueInputDtoValidator(),
            new BassRefundConfirmInputDtoValidator(clock),
            new BassRefundCancelInputDtoValidator());
    }

    /// <summary>Seeds an active contributor row.</summary>
    private static async Task<long> SeedContributorAsync(CnasDbContext db)
    {
        var c = new Contributor
        {
            Idno = "1003600099991",
            IdnoHash = "fake-hash-for-test-bass",
            Denumire = "SRL Payer",
            CreatedAtUtc = ClockNow.AddDays(-30),
            RegisteredAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        db.Contributors.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    /// <summary>Seeds a monthly contribution calculation with the supplied overpayment.</summary>
    private static async Task SeedMonthlyCalcAsync(
        CnasDbContext db,
        long contributorId,
        DateOnly month,
        decimal? overpaymentAmount)
    {
        db.MonthlyContributionCalculations.Add(new MonthlyContributionCalculation
        {
            ContributorId = contributorId,
            Month = month,
            TotalDeclared = 1_500m,
            TotalAdjusted = overpaymentAmount.HasValue ? 1_500m - overpaymentAmount.Value : 1_500m,
            OverpaymentAmount = overpaymentAmount,
            DeclarationCount = 1,
            CalculatedAtUtc = ClockNow.AddDays(-1),
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Builds the canonical request-input DTO.</summary>
    private static BassRefundRequestInputDto BuildRequestInput(long contributorId, decimal amount = 200m)
        => new(
            ContributorSqid: $"SQID-{contributorId}",
            RelatedMonth: ReportingMonth,
            RefundAmount: amount,
            AuthorisationDocumentReference: "DOC-123");

    // ───────── R0814 — RequestAsync ─────────

    /// <summary>R0814 — RequestAsync rejects when no positive overpayment exists for the (contributor, month).</summary>
    [Fact]
    public async Task RequestAsync_NoOverpayment_ReturnsNotFound()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        // No MonthlyContributionCalculation seeded — overpayment cannot be found.

        var result = await sut.RequestAsync(BuildRequestInput(payerId));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        result.ErrorMessage.Should().Be(BassRefundService.OverpaymentNotFoundMessage);
    }

    /// <summary>R0814 — RequestAsync rejects when an active refund already exists for the (contributor, month).</summary>
    [Fact]
    public async Task RequestAsync_ActiveRefundExists_ReturnsConflict()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        await SeedMonthlyCalcAsync(db, payerId, ReportingMonth, overpaymentAmount: 300m);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var first = await sut.RequestAsync(BuildRequestInput(payerId));
        first.IsSuccess.Should().BeTrue();
        var second = await sut.RequestAsync(BuildRequestInput(payerId));

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.Conflict);
        second.ErrorMessage.Should().Be(BassRefundService.ActiveRefundExistsMessage);
    }

    /// <summary>R0814 — RequestAsync happy path persists Requested + emits Notice audit.</summary>
    [Fact]
    public async Task RequestAsync_HappyPath_PersistsRequestedAndAuditsNotice()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        await SeedMonthlyCalcAsync(db, payerId, ReportingMonth, overpaymentAmount: 300m);
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.RequestAsync(BuildRequestInput(payerId, 250m));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(BassRefundStatus.Requested));
        result.Value.RefundAmount.Should().Be(250m);
        (await db.BassRefunds.CountAsync()).Should().Be(1);
        calls().Should().ContainSingle(c =>
            c.Code == BassRefundService.AuditRequested && c.Severity == AuditSeverity.Notice);
    }

    // ───────── R0814 — ApproveAsync ─────────

    /// <summary>R0814 — ApproveAsync transitions Requested → Approved and emits Critical audit.</summary>
    [Fact]
    public async Task ApproveAsync_HappyPath_FlipsApprovedAndCriticalAudit()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        await SeedMonthlyCalcAsync(db, payerId, ReportingMonth, overpaymentAmount: 300m);
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);
        await sut.RequestAsync(BuildRequestInput(payerId));
        var refundId = await db.BassRefunds.Select(r => r.Id).SingleAsync();

        var result = await sut.ApproveAsync(refundId);

        result.IsSuccess.Should().BeTrue();
        var refreshed = await db.BassRefunds.SingleAsync(r => r.Id == refundId);
        refreshed.Status.Should().Be(BassRefundStatus.Approved);
        refreshed.ApprovedDate.Should().NotBeNull();
        refreshed.ApprovedByUserId.Should().Be(1L);
        calls().Should().Contain(c =>
            c.Code == BassRefundService.AuditApproved && c.Severity == AuditSeverity.Critical);
    }

    // ───────── R0814 — IssueToTreasuryAsync ─────────

    /// <summary>R0814 — IssueToTreasuryAsync transitions Approved → IssuedToTreasury and stamps the dispatch ref.</summary>
    [Fact]
    public async Task IssueToTreasuryAsync_HappyPath_StampsDispatchReference()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        await SeedMonthlyCalcAsync(db, payerId, ReportingMonth, overpaymentAmount: 300m);
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);
        await sut.RequestAsync(BuildRequestInput(payerId));
        var refundId = await db.BassRefunds.Select(r => r.Id).SingleAsync();
        await sut.ApproveAsync(refundId);

        var result = await sut.IssueToTreasuryAsync(refundId, "TRS-2026-000123");

        result.IsSuccess.Should().BeTrue();
        var refreshed = await db.BassRefunds.SingleAsync(r => r.Id == refundId);
        refreshed.Status.Should().Be(BassRefundStatus.IssuedToTreasury);
        refreshed.TreasuryDispatchReference.Should().Be("TRS-2026-000123");
        refreshed.IssuedDate.Should().NotBeNull();
        calls().Should().Contain(c =>
            c.Code == BassRefundService.AuditIssued && c.Severity == AuditSeverity.Critical);
    }

    // ───────── R0814 — ConfirmAsync ─────────

    /// <summary>R0814 — ConfirmAsync transitions IssuedToTreasury → Confirmed.</summary>
    [Fact]
    public async Task ConfirmAsync_HappyPath_FlipsConfirmed()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        await SeedMonthlyCalcAsync(db, payerId, ReportingMonth, overpaymentAmount: 300m);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        await sut.RequestAsync(BuildRequestInput(payerId));
        var refundId = await db.BassRefunds.Select(r => r.Id).SingleAsync();
        await sut.ApproveAsync(refundId);
        await sut.IssueToTreasuryAsync(refundId, "TRS-2026-000123");

        var result = await sut.ConfirmAsync(refundId, new DateOnly(2026, 5, 22));

        result.IsSuccess.Should().BeTrue();
        var refreshed = await db.BassRefunds.SingleAsync(r => r.Id == refundId);
        refreshed.Status.Should().Be(BassRefundStatus.Confirmed);
        refreshed.ConfirmedDate.Should().Be(new DateOnly(2026, 5, 22));
    }

    // ───────── R0814 — CancelAsync ─────────

    /// <summary>R0814 — CancelAsync rejects when the row is already Confirmed.</summary>
    [Fact]
    public async Task CancelAsync_AlreadyConfirmed_ReturnsConflict()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        await SeedMonthlyCalcAsync(db, payerId, ReportingMonth, overpaymentAmount: 300m);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        await sut.RequestAsync(BuildRequestInput(payerId));
        var refundId = await db.BassRefunds.Select(r => r.Id).SingleAsync();
        await sut.ApproveAsync(refundId);
        await sut.IssueToTreasuryAsync(refundId, "TRS-1");
        await sut.ConfirmAsync(refundId, new DateOnly(2026, 5, 22));

        var result = await sut.CancelAsync(refundId, "Try to cancel after confirm.");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
        result.ErrorMessage.Should().Be(BassRefundService.InvalidStateMessage);
    }

    /// <summary>R0814 — CancelAsync from Requested flips to Cancelled with rationale + Critical audit.</summary>
    [Fact]
    public async Task CancelAsync_FromRequested_FlipsCancelledAndCriticalAudit()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        await SeedMonthlyCalcAsync(db, payerId, ReportingMonth, overpaymentAmount: 300m);
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);
        await sut.RequestAsync(BuildRequestInput(payerId));
        var refundId = await db.BassRefunds.Select(r => r.Id).SingleAsync();

        var result = await sut.CancelAsync(refundId, "Operator entered the wrong (payer, month).");

        result.IsSuccess.Should().BeTrue();
        var refreshed = await db.BassRefunds.SingleAsync(r => r.Id == refundId);
        refreshed.Status.Should().Be(BassRefundStatus.Cancelled);
        refreshed.CancelReason.Should().Be("Operator entered the wrong (payer, month).");
        refreshed.CancelledDate.Should().NotBeNull();
        calls().Should().Contain(c =>
            c.Code == BassRefundService.AuditCancelled && c.Severity == AuditSeverity.Critical);
    }
}
