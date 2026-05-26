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
/// R0817 / TOR BP 1.2-H — service-level tests for the staggered penalty
/// repayment workflow (<see cref="PenaltyRepaymentService"/>).
/// </summary>
public sealed class PenaltyRepaymentServiceTests
{
    /// <summary>Fixed UTC clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Today's date in UTC under the test clock.</summary>
    private static readonly DateOnly Today = DateOnly.FromDateTime(ClockNow);

    /// <summary>Stub clock returning the fixed instant.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-penalty-plan-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
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
    private static (IAuditService Audit, Func<List<(string Code, AuditSeverity Severity)>> Calls)
        NewAuditCapture()
    {
        var calls = new List<(string Code, AuditSeverity Severity)>();
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
                calls.Add((call.ArgAt<string>(0), call.ArgAt<AuditSeverity>(1)));
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
        caller.CorrelationId.Returns("corr-plan");
        caller.Roles.Returns((IReadOnlyCollection<string>)["cnas-admin"]);
        return caller;
    }

    /// <summary>Builds the SUT with every collaborator wired.</summary>
    private static PenaltyRepaymentService NewService(CnasDbContext db, IAuditService audit)
    {
        var clock = new StubClock(ClockNow);
        return new(
            db,
            clock,
            NewSqidMock(),
            NewCaller(),
            audit,
            new PenaltyRepaymentCreatePlanInputDtoValidator(clock),
            new PenaltyRepaymentRegisterPaymentInputDtoValidator(clock),
            new PenaltyRepaymentCancelPlanInputDtoValidator());
    }

    /// <summary>Seeds a late-payment-penalty row that the plan attaches to.</summary>
    private static async Task<LatePaymentPenalty> SeedPenaltyAsync(
        CnasDbContext db,
        decimal amount = 300m,
        bool waived = false)
    {
        var entity = new LatePaymentPenalty
        {
            ContributorId = 1L,
            Month = new DateOnly(2026, 4, 1),
            PrincipalAmount = 10_000m,
            CalculatedAtUtc = ClockNow.AddDays(-1),
            DueDate = new DateOnly(2026, 4, 25),
            UpToDate = Today,
            DaysLate = 27,
            DailyRatePercent = 0.03m,
            PenaltyAmount = amount,
            IsWaived = waived,
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        db.LatePaymentPenalties.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    /// <summary>Builds a canonical 3-installment create-plan input.</summary>
    private static PenaltyRepaymentCreatePlanInputDto BuildCreateInput(long penaltyId, int n = 3)
        => new(
            LatePaymentPenaltySqid: $"SQID-{penaltyId}",
            InstallmentCount: n,
            FirstInstallmentDueDate: Today.AddDays(1));

    // ───────── R0817 — CreatePlanAsync ─────────

    /// <summary>R0817 — CreatePlanAsync rejects when the penalty is waived.</summary>
    [Fact]
    public async Task CreatePlanAsync_PenaltyWaived_ReturnsConflict()
    {
        using var db = CreateContext();
        var penalty = await SeedPenaltyAsync(db, amount: 300m, waived: true);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.CreatePlanAsync(BuildCreateInput(penalty.Id));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
        result.ErrorMessage.Should().Be(PenaltyRepaymentService.PenaltyWaivedMessage);
    }

    /// <summary>R0817 — CreatePlanAsync generates N installments; the last absorbs the rounding residual.</summary>
    [Fact]
    public async Task CreatePlanAsync_HappyPath_GeneratesInstallmentsWithLastRowAbsorbingResidual()
    {
        using var db = CreateContext();
        // 100 / 3 == 33.33 each, residual 0.01 → final installment 33.34.
        var penalty = await SeedPenaltyAsync(db, amount: 100m);
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.CreatePlanAsync(BuildCreateInput(penalty.Id, n: 3));

        result.IsSuccess.Should().BeTrue();
        var planId = long.Parse(result.Value.Id["SQID-".Length..]);
        var installments = await db.PenaltyRepaymentInstallments
            .Where(i => i.PenaltyRepaymentPlanId == planId)
            .OrderBy(i => i.InstallmentNumber)
            .ToListAsync();
        installments.Should().HaveCount(3);
        installments[0].Amount.Should().Be(33.33m);
        installments[1].Amount.Should().Be(33.33m);
        installments[2].Amount.Should().Be(33.34m);
        installments.Sum(i => i.Amount).Should().Be(100m);
        calls().Should().ContainSingle(c =>
            c.Code == PenaltyRepaymentService.AuditCreated && c.Severity == AuditSeverity.Notice);
    }

    /// <summary>R0817 — CreatePlanAsync rejects when an Active plan already exists for the penalty.</summary>
    [Fact]
    public async Task CreatePlanAsync_ActivePlanExists_ReturnsConflict()
    {
        using var db = CreateContext();
        var penalty = await SeedPenaltyAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        (await sut.CreatePlanAsync(BuildCreateInput(penalty.Id))).IsSuccess.Should().BeTrue();

        var second = await sut.CreatePlanAsync(BuildCreateInput(penalty.Id));

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.Conflict);
        second.ErrorMessage.Should().Be(PenaltyRepaymentService.ActivePlanExistsMessage);
    }

    // ───────── R0817 — RegisterInstallmentPaymentAsync ─────────

    /// <summary>R0817 — paying an installment bumps PaidInstallmentCount and updates RemainingAmount.</summary>
    [Fact]
    public async Task RegisterInstallmentPaymentAsync_BumpsCounterAndRemainingAmount()
    {
        using var db = CreateContext();
        var penalty = await SeedPenaltyAsync(db, amount: 100m);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var createResult = await sut.CreatePlanAsync(BuildCreateInput(penalty.Id));
        var planId = long.Parse(createResult.Value.Id["SQID-".Length..]);
        var firstInstallment = await db.PenaltyRepaymentInstallments
            .Where(i => i.PenaltyRepaymentPlanId == planId && i.InstallmentNumber == 1)
            .SingleAsync();

        var result = await sut.RegisterInstallmentPaymentAsync(
            firstInstallment.Id, Today, paidAmount: 33.33m);

        result.IsSuccess.Should().BeTrue();
        var plan = await db.PenaltyRepaymentPlans.SingleAsync(p => p.Id == planId);
        plan.PaidInstallmentCount.Should().Be(1);
        plan.RemainingAmount.Should().Be(100m - 33.33m);
        plan.Status.Should().Be(PenaltyRepaymentPlanStatus.Active);
    }

    /// <summary>R0817 — paying the final installment flips the plan to Completed + Critical audit.</summary>
    [Fact]
    public async Task RegisterInstallmentPaymentAsync_LastInstallment_FlipsCompletedAndEmitsCriticalAudit()
    {
        using var db = CreateContext();
        var penalty = await SeedPenaltyAsync(db, amount: 100m);
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);
        var createResult = await sut.CreatePlanAsync(BuildCreateInput(penalty.Id, n: 3));
        var planId = long.Parse(createResult.Value.Id["SQID-".Length..]);
        var installments = await db.PenaltyRepaymentInstallments
            .Where(i => i.PenaltyRepaymentPlanId == planId)
            .OrderBy(i => i.InstallmentNumber)
            .ToListAsync();

        await sut.RegisterInstallmentPaymentAsync(installments[0].Id, Today, 33.33m);
        await sut.RegisterInstallmentPaymentAsync(installments[1].Id, Today, 33.33m);
        var last = await sut.RegisterInstallmentPaymentAsync(installments[2].Id, Today, 33.34m);

        last.IsSuccess.Should().BeTrue();
        var plan = await db.PenaltyRepaymentPlans.SingleAsync(p => p.Id == planId);
        plan.Status.Should().Be(PenaltyRepaymentPlanStatus.Completed);
        plan.CompletedUtc.Should().NotBeNull();
        calls().Should().Contain(c =>
            c.Code == PenaltyRepaymentService.AuditCompleted && c.Severity == AuditSeverity.Critical);
    }

    // ───────── R0817 — MarkDefaultedAsync ─────────

    /// <summary>R0817 — MarkDefaultedAsync flips Active → Defaulted and emits Critical audit.</summary>
    [Fact]
    public async Task MarkDefaultedAsync_HappyPath_FlipsDefaultedAndCriticalAudit()
    {
        using var db = CreateContext();
        var penalty = await SeedPenaltyAsync(db);
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);
        var createResult = await sut.CreatePlanAsync(BuildCreateInput(penalty.Id));
        var planId = long.Parse(createResult.Value.Id["SQID-".Length..]);

        var result = await sut.MarkDefaultedAsync(planId);

        result.IsSuccess.Should().BeTrue();
        var plan = await db.PenaltyRepaymentPlans.SingleAsync(p => p.Id == planId);
        plan.Status.Should().Be(PenaltyRepaymentPlanStatus.Defaulted);
        calls().Should().Contain(c =>
            c.Code == PenaltyRepaymentService.AuditDefaulted && c.Severity == AuditSeverity.Critical);
    }

    // ───────── R0817 — CancelPlanAsync ─────────

    /// <summary>R0817 — CancelPlanAsync rejects when the plan is already Completed.</summary>
    [Fact]
    public async Task CancelPlanAsync_AlreadyCompleted_ReturnsConflict()
    {
        using var db = CreateContext();
        var penalty = await SeedPenaltyAsync(db, amount: 60m);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var createResult = await sut.CreatePlanAsync(BuildCreateInput(penalty.Id, n: 2));
        var planId = long.Parse(createResult.Value.Id["SQID-".Length..]);
        var installments = await db.PenaltyRepaymentInstallments
            .Where(i => i.PenaltyRepaymentPlanId == planId)
            .OrderBy(i => i.InstallmentNumber).ToListAsync();
        await sut.RegisterInstallmentPaymentAsync(installments[0].Id, Today, 30m);
        await sut.RegisterInstallmentPaymentAsync(installments[1].Id, Today, 30m);

        var result = await sut.CancelPlanAsync(planId, "Try to cancel after complete.");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
        result.ErrorMessage.Should().Be(PenaltyRepaymentService.InvalidStateMessage);
    }
}
