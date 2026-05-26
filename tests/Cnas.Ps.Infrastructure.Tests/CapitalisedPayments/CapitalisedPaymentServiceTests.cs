using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.CapitalisedPayments;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.CapitalisedPayments;
using Cnas.Ps.Infrastructure.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.CapitalisedPayments;

/// <summary>
/// R1202 / TOR §3.4-C — service-level tests for the capitalised-payment
/// registry. Exercises the create / submit / compute / approve / reject /
/// settle / cancel state-machine, ensures audit + metric emission, and
/// verifies the auto-generated request-number format.
/// </summary>
public sealed class CapitalisedPaymentServiceTests
{
    /// <summary>Fixed UTC clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-cap-pay-{Guid.NewGuid():N}")
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

    /// <summary>Sqid stub that round-trips "CPR-{id}" strings.</summary>
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"CPR-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            var s = call.Arg<string>();
            if (s is not null && s.StartsWith("CPR-", StringComparison.Ordinal)
                && long.TryParse(s["CPR-".Length..], out var id))
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

    /// <summary>Authenticated caller stub.</summary>
    private static ICallerContext NewCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(7L);
        caller.UserSqid.Returns("USR-7");
        caller.SourceIp.Returns("203.0.113.7");
        caller.CorrelationId.Returns("corr-cap");
        caller.Roles.Returns((IReadOnlyCollection<string>)["cnas-admin"]);
        return caller;
    }

    /// <summary>Builds the SUT.</summary>
    private static CapitalisedPaymentService NewService(CnasDbContext db, IAuditService audit)
    {
        var clock = new StubClock(ClockNow);
        var mortality = new MoldovaPlaceholderMortalityTable();
        var calculator = new PresentValueAnnuityCalculator(mortality);
        return new(
            db,
            clock,
            NewSqidMock(),
            NewCaller(),
            audit,
            IdHashHelper.Instance,
            calculator,
            new CapitalisedPaymentRequestCreateInputValidator(clock),
            new CapitalisedPaymentRequestModifyInputValidator(clock),
            new CapitalisedPaymentReasonInputValidator(),
            new CapitalisedPaymentApprovalInputValidator(),
            new CapitalisedPaymentSettlementInputValidator(),
            new CapitalisedPaymentRequestFilterValidator());
    }

    /// <summary>Builds a canonical create-input DTO.</summary>
    private static CapitalisedPaymentRequestCreateInputDto BuildCreateInput(
        string idnp = "2002000000007",
        string idno = "1003600000123") => new(
            BeneficiaryIdnp: idnp,
            BeneficiaryBirthDate: new DateOnly(1965, 4, 1),
            BeneficiarySex: nameof(BeneficiarySex.Male),
            LiquidatedDebtorIdno: idno,
            LiquidatedDebtorName: "SRL Test în lichidare",
            ObligationKind: nameof(CapitalisedPaymentObligationKind.IncapacityForWork),
            MonthlyAmountMdl: 1_500m,
            ObligationStartDate: new DateOnly(2015, 1, 1),
            ObligationEndDate: new DateOnly(2030, 1, 1),
            ValuationDate: new DateOnly(2026, 6, 1),
            LegalDiscountRatePercent: 8m);

    // ────────── CreateAsync ──────────

    [Fact]
    public async Task CreateAsync_HappyPath_PersistsDraftAndAuditsAndGeneratesNumber()
    {
        var db = CreateContext();
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.CreateAsync(BuildCreateInput());

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(CapitalisedPaymentRequestStatus.Draft));
        result.Value.RequestNumber.Should().Be("CPR-2026-000001");
        // The hash is returned externally (not the plaintext IDNP).
        result.Value.BeneficiaryIdnpHash.Should().NotBeNullOrWhiteSpace();
        (await db.CapitalisedPaymentRequests.CountAsync()).Should().Be(1);
        calls().Should().ContainSingle(c =>
            c.Code == CapitalisedPaymentService.AuditCreated
            && c.Severity == AuditSeverity.Critical);
    }

    [Fact]
    public async Task SubmitAsync_FromNonDraft_ReturnsConflict()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var created = await sut.CreateAsync(BuildCreateInput());
        await sut.SubmitAsync(created.Value.Id); // Draft → Submitted

        var second = await sut.SubmitAsync(created.Value.Id); // Submitted → ??? — invalid

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    [Fact]
    public async Task ComputeAsync_PersistsDecisionAndTransitionsState()
    {
        var db = CreateContext();
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);
        var created = await sut.CreateAsync(BuildCreateInput());
        await sut.SubmitAsync(created.Value.Id);

        var compute = await sut.ComputeAsync(created.Value.Id);

        compute.IsSuccess.Should().BeTrue();
        compute.Value.CapitalisedAmountMdl.Should().BeGreaterThan(0m);
        compute.Value.LifeExpectancyMonths.Should().BeGreaterThan(0);

        var requestId = (await db.CapitalisedPaymentRequests.SingleAsync()).Id;
        var refreshed = await db.CapitalisedPaymentRequests.FindAsync(requestId);
        refreshed!.Status.Should().Be(CapitalisedPaymentRequestStatus.ComputedAwaitingApproval);
        (await db.CapitalisedPaymentDecisions.CountAsync()).Should().Be(1);
        calls().Should().Contain(c =>
            c.Code == CapitalisedPaymentService.AuditComputed
            && c.Severity == AuditSeverity.Critical);
    }

    [Fact]
    public async Task ApproveAsync_FlipsRequestAndDecisionStatus()
    {
        var db = CreateContext();
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);
        var created = await sut.CreateAsync(BuildCreateInput());
        await sut.SubmitAsync(created.Value.Id);
        await sut.ComputeAsync(created.Value.Id);

        var approve = await sut.ApproveAsync(
            created.Value.Id,
            new CapitalisedPaymentApprovalInputDto("Approving capitalised amount per liquidation file."));

        approve.IsSuccess.Should().BeTrue();
        approve.Value.DecisionStatus.Should().Be(nameof(CapitalisedPaymentDecisionStatus.Approved));
        var refreshed = await db.CapitalisedPaymentRequests.SingleAsync();
        refreshed.Status.Should().Be(CapitalisedPaymentRequestStatus.Approved);
        calls().Should().Contain(c =>
            c.Code == CapitalisedPaymentService.AuditApproved
            && c.Severity == AuditSeverity.Critical);
    }

    [Fact]
    public async Task RejectAsync_PersistsRejectionReasonAndUpdatesDecision()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var created = await sut.CreateAsync(BuildCreateInput());
        await sut.SubmitAsync(created.Value.Id);
        await sut.ComputeAsync(created.Value.Id);

        var reject = await sut.RejectAsync(
            created.Value.Id,
            new CapitalisedPaymentReasonInputDto("Calculator inputs disputed — reject pending re-check."));

        reject.IsSuccess.Should().BeTrue();
        reject.Value.DecisionStatus.Should().Be(nameof(CapitalisedPaymentDecisionStatus.Rejected));
        reject.Value.RejectionReason.Should().StartWith("Calculator inputs disputed");
        var refreshed = await db.CapitalisedPaymentRequests.SingleAsync();
        refreshed.Status.Should().Be(CapitalisedPaymentRequestStatus.Rejected);
    }

    [Fact]
    public async Task MarkSettledAsync_RequiresApprovedStatus()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var created = await sut.CreateAsync(BuildCreateInput());

        var settle = await sut.MarkSettledAsync(
            created.Value.Id,
            new CapitalisedPaymentSettlementInputDto(
                TreasuryReceiptSqid: "TRSY-1",
                SettlementNote: "Treasury booked the receipt."));

        settle.IsFailure.Should().BeTrue();
        settle.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    [Fact]
    public async Task CancelAsync_RecordsReasonAndTransitionsToCancelled()
    {
        var db = CreateContext();
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);
        var created = await sut.CreateAsync(BuildCreateInput());

        var cancel = await sut.CancelAsync(
            created.Value.Id,
            new CapitalisedPaymentReasonInputDto("Liquidation file withdrawn by debtor."));

        cancel.IsSuccess.Should().BeTrue();
        cancel.Value.Status.Should().Be(nameof(CapitalisedPaymentRequestStatus.Cancelled));
        cancel.Value.CancellationReason.Should().StartWith("Liquidation file withdrawn");
        calls().Should().Contain(c =>
            c.Code == CapitalisedPaymentService.AuditCancelled
            && c.Severity == AuditSeverity.Critical);
    }
}
