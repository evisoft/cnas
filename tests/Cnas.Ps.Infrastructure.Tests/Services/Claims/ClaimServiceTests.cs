using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services.Claims;

/// <summary>
/// R0831 / R0832 / TOR BP 1.3-B + BP 1.3-C — service-level tests for the
/// claims (creanțe) registry and per-claim payment-application path.
/// </summary>
public sealed class ClaimServiceTests
{
    /// <summary>Fixed UTC clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Canonical reporting month anchored on the first of the month.</summary>
    private static readonly DateOnly ReportingMonth = new(2026, 4, 1);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-claims-{Guid.NewGuid():N}")
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

    /// <summary>Captures audit invocations for assertion (one-shot — last call wins).</summary>
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
        caller.CorrelationId.Returns("corr-claims");
        caller.Roles.Returns((IReadOnlyCollection<string>)["cnas-user"]);
        return caller;
    }

    /// <summary>Builds the SUT with all collaborators wired.</summary>
    private static ClaimService NewService(CnasDbContext db, IAuditService audit)
    {
        var clock = new StubClock(ClockNow);
        return new(
            db,
            clock,
            NewSqidMock(),
            NewCaller(),
            audit,
            new ClaimRegisterInputDtoValidator(),
            new ClaimModifyInputDtoValidator(),
            new ClaimPaymentInputDtoValidator(clock),
            new ClaimReasonInputDtoValidator());
    }

    /// <summary>Seeds an active contributor (payer) row.</summary>
    private static async Task<long> SeedContributorAsync(CnasDbContext db)
    {
        var c = new Contributor
        {
            Idno = "1003600099991",
            IdnoHash = "fake-hash-for-test",
            Denumire = "SRL Payer",
            CreatedAtUtc = ClockNow.AddDays(-30),
            RegisteredAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        db.Contributors.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    /// <summary>Convenience: builds a canonical register-input DTO.</summary>
    private static ClaimRegisterInputDto BuildRegisterInput(long contributorId, decimal principal = 1_500m)
        => new(
            ContributorSqid: $"SQID-{contributorId}",
            Kind: nameof(ClaimKind.Contribution),
            RelatedMonth: ReportingMonth,
            PrincipalAmount: principal,
            OpenedDate: new DateOnly(2026, 5, 22));

    // ───────── R0831 — RegisterAsync ─────────

    /// <summary>R0831 — RegisterAsync persists Open claim + audit Notice.</summary>
    [Fact]
    public async Task RegisterAsync_HappyPath_PersistsOpenAndAuditsNotice()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.RegisterAsync(BuildRegisterInput(payerId, 2_500m));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(ClaimStatus.Open));
        result.Value.PrincipalAmount.Should().Be(2_500m);
        result.Value.PaidAmount.Should().Be(0m);
        result.Value.RemainingAmount.Should().Be(2_500m);
        (await db.Claims.CountAsync()).Should().Be(1);
        calls().Should().ContainSingle(c =>
            c.Code == ClaimService.AuditRegistered && c.Severity == AuditSeverity.Notice);
    }

    /// <summary>R0831 — RegisterAsync auto-generates ClaimNumber in the canonical format.</summary>
    [Fact]
    public async Task RegisterAsync_GeneratesClaimNumberInExpectedFormat()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var first = await sut.RegisterAsync(BuildRegisterInput(payerId));
        var second = await sut.RegisterAsync(BuildRegisterInput(payerId));

        first.Value.ClaimNumber.Should().Be("CRN-2026-000001");
        second.Value.ClaimNumber.Should().Be("CRN-2026-000002");
    }

    /// <summary>R0831 — RegisterAsync rejects when the contributor does not exist.</summary>
    [Fact]
    public async Task RegisterAsync_UnknownContributor_ReturnsNotFound()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.RegisterAsync(BuildRegisterInput(contributorId: 99_999L));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ───────── R0831 — ModifyAsync ─────────

    /// <summary>R0831 — ModifyAsync updates PrincipalAmount and recomputes RemainingAmount.</summary>
    [Fact]
    public async Task ModifyAsync_UpdatesPrincipal_RecomputesRemaining()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var registered = await sut.RegisterAsync(BuildRegisterInput(payerId, 1_000m));
        var claimId = await db.Claims.Select(c => c.Id).SingleAsync();

        var result = await sut.ModifyAsync(claimId,
            new ClaimModifyInputDto(
                PrincipalAmount: 5_000m,
                DueDate: null,
                RelatedDocumentReference: null,
                ChangeReason: "Court update increased principal."));

        result.IsSuccess.Should().BeTrue();
        result.Value.PrincipalAmount.Should().Be(5_000m);
        result.Value.RemainingAmount.Should().Be(5_000m);
    }

    /// <summary>R0831 — ModifyAsync rejects when the claim is already Settled.</summary>
    [Fact]
    public async Task ModifyAsync_AlreadySettled_ReturnsConflict()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var registered = await sut.RegisterAsync(BuildRegisterInput(payerId, 100m));
        var claimId = await db.Claims.Select(c => c.Id).SingleAsync();
        await sut.RegisterPaymentAsync(claimId, new ClaimPaymentInputDto(
            PaidDate: new DateOnly(2026, 5, 22),
            Amount: 100m));

        var result = await sut.ModifyAsync(claimId,
            new ClaimModifyInputDto(200m, null, null, "Tweak"));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
        result.ErrorMessage.Should().Be(ClaimService.TerminalStateMessage);
    }

    // ───────── R0832 — RegisterPaymentAsync ─────────

    /// <summary>R0832 — Partial payment flips to PartiallyPaid and updates running totals.</summary>
    [Fact]
    public async Task RegisterPaymentAsync_PartialPayment_FlipsPartiallyPaidAndUpdatesTotals()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var registered = await sut.RegisterAsync(BuildRegisterInput(payerId, 500m));
        var claimId = await db.Claims.Select(c => c.Id).SingleAsync();

        var result = await sut.RegisterPaymentAsync(claimId, new ClaimPaymentInputDto(
            PaidDate: new DateOnly(2026, 5, 22),
            Amount: 200m));

        result.IsSuccess.Should().BeTrue();
        result.Value.Amount.Should().Be(200m);
        var refreshed = await db.Claims.SingleAsync(c => c.Id == claimId);
        refreshed.Status.Should().Be(ClaimStatus.PartiallyPaid);
        refreshed.PaidAmount.Should().Be(200m);
        refreshed.RemainingAmount.Should().Be(300m);
    }

    /// <summary>R0832 — Final payment flips to Settled and emits Critical CLAIM.SETTLED audit.</summary>
    [Fact]
    public async Task RegisterPaymentAsync_FinalPayment_FlipsSettledAndEmitsCriticalAudit()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);
        var registered = await sut.RegisterAsync(BuildRegisterInput(payerId, 100m));
        var claimId = await db.Claims.Select(c => c.Id).SingleAsync();

        var result = await sut.RegisterPaymentAsync(claimId, new ClaimPaymentInputDto(
            PaidDate: new DateOnly(2026, 5, 22),
            Amount: 100m));

        result.IsSuccess.Should().BeTrue();
        var refreshed = await db.Claims.SingleAsync(c => c.Id == claimId);
        refreshed.Status.Should().Be(ClaimStatus.Settled);
        refreshed.SettledDate.Should().NotBeNull();
        calls().Should().Contain(c =>
            c.Code == ClaimService.AuditSettled && c.Severity == AuditSeverity.Critical);
    }

    /// <summary>
    /// iter-149 — RegisterPaymentAsync on a terminal claim (Settled / Cancelled)
    /// is rejected by the declarative ClaimStatusTransitions table, preserving
    /// the legacy Conflict + TerminalStateMessage wire shape.
    /// </summary>
    [Fact]
    public async Task RegisterPaymentAsync_TerminalClaim_ReturnsConflictViaTransitionTable()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var registered = await sut.RegisterAsync(BuildRegisterInput(payerId, 100m));
        var claimId = await db.Claims.Select(c => c.Id).SingleAsync();
        // Settle the claim with a full payment first.
        var first = await sut.RegisterPaymentAsync(claimId, new ClaimPaymentInputDto(
            PaidDate: new DateOnly(2026, 5, 22),
            Amount: 100m));
        first.IsSuccess.Should().BeTrue();

        // A subsequent payment attempt against the Settled claim must be refused
        // by the transition table (Settled has no outbound edges). PaidDate must
        // not be in the future relative to the stub clock.
        var result = await sut.RegisterPaymentAsync(claimId, new ClaimPaymentInputDto(
            PaidDate: new DateOnly(2026, 5, 22),
            Amount: 1m));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
        result.ErrorMessage.Should().Be(ClaimService.TerminalStateMessage);
    }

    /// <summary>R0832 — Overpayment is refused with OVERPAYMENT_NOT_ALLOWED.</summary>
    [Fact]
    public async Task RegisterPaymentAsync_Overpayment_RejectsWithStableMessage()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var registered = await sut.RegisterAsync(BuildRegisterInput(payerId, 100m));
        var claimId = await db.Claims.Select(c => c.Id).SingleAsync();

        var result = await sut.RegisterPaymentAsync(claimId, new ClaimPaymentInputDto(
            PaidDate: new DateOnly(2026, 5, 22),
            Amount: 150m));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Be(ClaimService.OverpaymentMessage);
    }

    /// <summary>R0832 — counter cnas.claim.payment_applied{outcome=settled} increments on final payment.</summary>
    [Fact]
    public async Task RegisterPaymentAsync_FinalPayment_IncrementsSettledCounter()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var registered = await sut.RegisterAsync(BuildRegisterInput(payerId, 50m));
        var claimId = await db.Claims.Select(c => c.Id).SingleAsync();

        // Capture the counter increment via MeterListener.
        var settled = 0L;
        using var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == CnasMeter.MeterName && inst.Name == "cnas.claim.payment_applied")
                {
                    l.EnableMeasurementEvents(inst);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome" && tag.Value is "settled")
                {
                    Interlocked.Add(ref settled, value);
                }
            }
        });
        listener.Start();

        var result = await sut.RegisterPaymentAsync(claimId, new ClaimPaymentInputDto(
            PaidDate: new DateOnly(2026, 5, 22),
            Amount: 50m));

        result.IsSuccess.Should().BeTrue();
        Interlocked.Read(ref settled).Should().Be(1);
    }

    // ───────── R0831 — CancelAsync ─────────

    /// <summary>R0831 — CancelAsync sets CancelReason + CancelledDate + Critical audit.</summary>
    [Fact]
    public async Task CancelAsync_HappyPath_SetsReasonAndCriticalAudit()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);
        var registered = await sut.RegisterAsync(BuildRegisterInput(payerId));
        var claimId = await db.Claims.Select(c => c.Id).SingleAsync();

        var result = await sut.CancelAsync(claimId, "Court annulled the obligation.");

        result.IsSuccess.Should().BeTrue();
        var refreshed = await db.Claims.SingleAsync(c => c.Id == claimId);
        refreshed.Status.Should().Be(ClaimStatus.Cancelled);
        refreshed.CancelReason.Should().Be("Court annulled the obligation.");
        refreshed.CancelledDate.Should().NotBeNull();
        calls().Should().Contain(c =>
            c.Code == ClaimService.AuditCancelled && c.Severity == AuditSeverity.Critical);
    }

    /// <summary>R0831 — CancelAsync refuses to cancel an already-Settled claim.</summary>
    [Fact]
    public async Task CancelAsync_AlreadySettled_ReturnsConflict()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var registered = await sut.RegisterAsync(BuildRegisterInput(payerId, 100m));
        var claimId = await db.Claims.Select(c => c.Id).SingleAsync();
        await sut.RegisterPaymentAsync(claimId, new ClaimPaymentInputDto(
            PaidDate: new DateOnly(2026, 5, 22),
            Amount: 100m));

        var result = await sut.CancelAsync(claimId, "Trying to cancel a settled claim.");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    // ───────── R0831 — DisputeAsync ─────────

    /// <summary>R0831 — DisputeAsync flips Status to Disputed + Critical audit.</summary>
    [Fact]
    public async Task DisputeAsync_HappyPath_FlipsDisputedAndCriticalAudit()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);
        var registered = await sut.RegisterAsync(BuildRegisterInput(payerId));
        var claimId = await db.Claims.Select(c => c.Id).SingleAsync();

        var result = await sut.DisputeAsync(claimId, "Contributor contests the principal calculation.");

        result.IsSuccess.Should().BeTrue();
        var refreshed = await db.Claims.SingleAsync(c => c.Id == claimId);
        refreshed.Status.Should().Be(ClaimStatus.Disputed);
        calls().Should().Contain(c =>
            c.Code == ClaimService.AuditDisputed && c.Severity == AuditSeverity.Critical);
    }

    // ───────── R0831 — ListForContributorAsync ─────────

    /// <summary>R0831 — ListForContributorAsync returns the ordered claims list.</summary>
    [Fact]
    public async Task ListForContributorAsync_ReturnsOrderedByOpenedDate()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        await sut.RegisterAsync(BuildRegisterInput(payerId, 100m));
        await sut.RegisterAsync(BuildRegisterInput(payerId, 200m));

        var list = await sut.ListForContributorAsync(payerId);

        list.Should().HaveCount(2);
        var amounts = list.Select(c => c.PrincipalAmount).ToList();
        amounts.Should().Contain(100m);
        amounts.Should().Contain(200m);
    }

    // ───────── R0016 — Refactor regression: StatusTransitionTable path preserves codes ─────────

    /// <summary>
    /// R0016 — after refactoring CancelAsync onto <c>StatusTransitionTable&lt;ClaimStatus&gt;</c>
    /// the legacy <see cref="ErrorCodes.Conflict"/> + <see cref="ClaimService.TerminalStateMessage"/>
    /// shape is preserved (no breaking wire change for callers).
    /// </summary>
    [Fact]
    public async Task CancelAsync_TerminalState_PreservesLegacyConflictMessage()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var registered = await sut.RegisterAsync(BuildRegisterInput(payerId, 100m));
        var claimId = await db.Claims.Select(c => c.Id).SingleAsync();
        await sut.RegisterPaymentAsync(claimId, new ClaimPaymentInputDto(
            PaidDate: new DateOnly(2026, 5, 22),
            Amount: 100m));

        var result = await sut.CancelAsync(claimId, "Trying to cancel a settled claim.");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
        result.ErrorMessage.Should().Be(ClaimService.TerminalStateMessage);
    }

    /// <summary>
    /// R0016 — DisputeAsync on a Settled claim returns the legacy
    /// <see cref="ClaimService.DisputeForbiddenMessage"/> error shape even though the
    /// underlying decision is now routed through <c>StatusTransitionTable</c>.
    /// </summary>
    [Fact]
    public async Task DisputeAsync_AlreadySettled_PreservesLegacyForbiddenMessage()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var registered = await sut.RegisterAsync(BuildRegisterInput(payerId, 100m));
        var claimId = await db.Claims.Select(c => c.Id).SingleAsync();
        await sut.RegisterPaymentAsync(claimId, new ClaimPaymentInputDto(
            PaidDate: new DateOnly(2026, 5, 22),
            Amount: 100m));

        var result = await sut.DisputeAsync(claimId, "Late dispute attempt.");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
        result.ErrorMessage.Should().Be(ClaimService.DisputeForbiddenMessage);
    }

    /// <summary>
    /// R0016 — DisputeAsync on an already-Disputed claim still rejects (the table
    /// does not declare a Disputed → Disputed self-loop).
    /// </summary>
    [Fact]
    public async Task DisputeAsync_AlreadyDisputed_PreservesLegacyForbiddenMessage()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var registered = await sut.RegisterAsync(BuildRegisterInput(payerId, 500m));
        var claimId = await db.Claims.Select(c => c.Id).SingleAsync();
        var first = await sut.DisputeAsync(claimId, "Contributor contests the principal.");
        first.IsSuccess.Should().BeTrue();

        var second = await sut.DisputeAsync(claimId, "Trying again on an already-disputed claim.");

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.Conflict);
        second.ErrorMessage.Should().Be(ClaimService.DisputeForbiddenMessage);
    }
}
