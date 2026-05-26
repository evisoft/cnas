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
/// R0815 / TOR BP 1.2-F — service-level tests for
/// <see cref="PaymentCorrectionService"/> covering each lifecycle transition
/// and each kind-specific receipt mutation path.
/// </summary>
public sealed class PaymentCorrectionServiceTests
{
    /// <summary>Fixed UTC clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Canonical reporting month anchored on the first of the month.</summary>
    private static readonly DateOnly ReportingMonth = new(2026, 4, 1);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-payment-corrections-{Guid.NewGuid():N}")
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
        caller.CorrelationId.Returns("corr-pc");
        caller.Roles.Returns((IReadOnlyCollection<string>)["cnas-admin"]);
        return caller;
    }

    /// <summary>Builds the SUT with all collaborators wired.</summary>
    private static PaymentCorrectionService NewService(CnasDbContext db, IAuditService audit)
    {
        var clock = new StubClock(ClockNow);
        return new(
            db,
            clock,
            NewSqidMock(),
            NewCaller(),
            audit,
            new PaymentCorrectionCreateInputDtoValidator(),
            new PaymentCorrectionCancelInputDtoValidator());
    }

    /// <summary>Seeds an active contributor row.</summary>
    private static async Task<long> SeedContributorAsync(CnasDbContext db, string idnoSuffix)
    {
        var c = new Contributor
        {
            Idno = $"100360009{idnoSuffix}",
            IdnoHash = $"fake-hash-{idnoSuffix}",
            Denumire = $"SRL Payer {idnoSuffix}",
            CreatedAtUtc = ClockNow.AddDays(-30),
            RegisteredAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        db.Contributors.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    /// <summary>Seeds a Treasury payment receipt row.</summary>
    private static async Task<long> SeedReceiptAsync(
        CnasDbContext db,
        long payerId,
        decimal amount = 1_000m)
    {
        var receipt = new TreasuryPaymentReceipt
        {
            TreasuryReferenceNumber = $"TRS-{Guid.NewGuid():N}",
            ReceiptDate = new DateOnly(2026, 5, 1),
            PayerContributorId = payerId,
            ReportingMonth = ReportingMonth,
            AmountReceived = amount,
            DistributionStatus = TreasuryPaymentDistributionStatus.Distributed,
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        db.TreasuryPaymentReceipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt.Id;
    }

    // ───────── R0815 — CreateAsync ─────────

    /// <summary>R0815 — CreateAsync (Reverse) creates a Draft row + Notice audit.</summary>
    [Fact]
    public async Task CreateAsync_Reverse_CreatesDraftAndAudits()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db, "1");
        var receiptId = await SeedReceiptAsync(db, payerId);
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.CreateAsync(new PaymentCorrectionCreateInputDto(
            OriginalReceiptSqid: $"SQID-{receiptId}",
            Kind: nameof(PaymentCorrectionKind.Reverse),
            Reason: "Receipt was duplicated by the Treasury feed."));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(PaymentCorrectionStatus.Draft));
        result.Value.Kind.Should().Be(nameof(PaymentCorrectionKind.Reverse));
        calls().Should().ContainSingle(c =>
            c.Code == PaymentCorrectionService.AuditCreated && c.Severity == AuditSeverity.Notice);
    }

    /// <summary>R0815 — CreateAsync (RedirectToPayer) rejects without a redirect-target Sqid.</summary>
    [Fact]
    public async Task CreateAsync_RedirectToPayer_MissingTarget_ReturnsValidationFailed()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db, "1");
        var receiptId = await SeedReceiptAsync(db, payerId);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.CreateAsync(new PaymentCorrectionCreateInputDto(
            OriginalReceiptSqid: $"SQID-{receiptId}",
            Kind: nameof(PaymentCorrectionKind.RedirectToPayer),
            Reason: "Wrong-payer mis-route."));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>R0815 — CreateAsync (AdjustAmount) rejects when AdjustedAmount exceeds the original receipt amount.</summary>
    [Fact]
    public async Task CreateAsync_AdjustAmount_ExceedsOriginal_ReturnsValidationFailed()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db, "1");
        var receiptId = await SeedReceiptAsync(db, payerId, amount: 1_000m);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.CreateAsync(new PaymentCorrectionCreateInputDto(
            OriginalReceiptSqid: $"SQID-{receiptId}",
            Kind: nameof(PaymentCorrectionKind.AdjustAmount),
            Reason: "Over-adjusted amount.",
            AdjustedAmount: 1_500m));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Be(PaymentCorrectionService.AdjustedAmountExceedsOriginalMessage);
    }

    // ───────── R0815 — ApproveAsync ─────────

    /// <summary>R0815 — ApproveAsync transitions Draft → Approved + Critical audit.</summary>
    [Fact]
    public async Task ApproveAsync_HappyPath_FlipsApprovedAndCriticalAudit()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db, "1");
        var receiptId = await SeedReceiptAsync(db, payerId);
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);
        await sut.CreateAsync(new PaymentCorrectionCreateInputDto(
            OriginalReceiptSqid: $"SQID-{receiptId}",
            Kind: nameof(PaymentCorrectionKind.Reverse),
            Reason: "Duplicate receipt."));
        var correctionId = await db.PaymentCorrections.Select(c => c.Id).SingleAsync();

        var result = await sut.ApproveAsync(correctionId);

        result.IsSuccess.Should().BeTrue();
        var refreshed = await db.PaymentCorrections.SingleAsync(c => c.Id == correctionId);
        refreshed.Status.Should().Be(PaymentCorrectionStatus.Approved);
        refreshed.ApprovedByUserId.Should().Be(1L);
        calls().Should().Contain(c =>
            c.Code == PaymentCorrectionService.AuditApproved && c.Severity == AuditSeverity.Critical);
    }

    // ───────── R0815 — ApplyAsync ─────────

    /// <summary>R0815 — ApplyAsync (Reverse) sets the receipt's DistributionStatus to Failed.</summary>
    [Fact]
    public async Task ApplyAsync_Reverse_SetsReceiptDistributionStatusFailed()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db, "1");
        var receiptId = await SeedReceiptAsync(db, payerId, amount: 1_000m);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        await sut.CreateAsync(new PaymentCorrectionCreateInputDto(
            OriginalReceiptSqid: $"SQID-{receiptId}",
            Kind: nameof(PaymentCorrectionKind.Reverse),
            Reason: "Duplicate receipt."));
        var correctionId = await db.PaymentCorrections.Select(c => c.Id).SingleAsync();
        await sut.ApproveAsync(correctionId);

        var result = await sut.ApplyAsync(correctionId);

        result.IsSuccess.Should().BeTrue();
        var receipt = await db.TreasuryPaymentReceipts.SingleAsync(r => r.Id == receiptId);
        receipt.DistributionStatus.Should().Be(TreasuryPaymentDistributionStatus.Failed);
        receipt.UndistributedRemainderAmount.Should().Be(1_000m);
        var correction = await db.PaymentCorrections.SingleAsync(c => c.Id == correctionId);
        correction.Status.Should().Be(PaymentCorrectionStatus.Applied);
        correction.AppliedUtc.Should().NotBeNull();
    }

    /// <summary>R0815 — ApplyAsync (RedirectToMonth) updates the receipt's ReportingMonth.</summary>
    [Fact]
    public async Task ApplyAsync_RedirectToMonth_UpdatesReceiptReportingMonth()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db, "1");
        var receiptId = await SeedReceiptAsync(db, payerId);
        var newMonth = new DateOnly(2026, 5, 1);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        await sut.CreateAsync(new PaymentCorrectionCreateInputDto(
            OriginalReceiptSqid: $"SQID-{receiptId}",
            Kind: nameof(PaymentCorrectionKind.RedirectToMonth),
            Reason: "Operator originally booked the wrong reporting month.",
            RedirectedToMonth: newMonth));
        var correctionId = await db.PaymentCorrections.Select(c => c.Id).SingleAsync();
        await sut.ApproveAsync(correctionId);

        var result = await sut.ApplyAsync(correctionId);

        result.IsSuccess.Should().BeTrue();
        var receipt = await db.TreasuryPaymentReceipts.SingleAsync(r => r.Id == receiptId);
        receipt.ReportingMonth.Should().Be(newMonth);
    }

    /// <summary>R0815 — ApplyAsync rejects when the row is still Draft (must be Approved first).</summary>
    [Fact]
    public async Task ApplyAsync_StillDraft_ReturnsConflict()
    {
        var db = CreateContext();
        var payerId = await SeedContributorAsync(db, "1");
        var receiptId = await SeedReceiptAsync(db, payerId);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        await sut.CreateAsync(new PaymentCorrectionCreateInputDto(
            OriginalReceiptSqid: $"SQID-{receiptId}",
            Kind: nameof(PaymentCorrectionKind.Reverse),
            Reason: "Duplicate receipt."));
        var correctionId = await db.PaymentCorrections.Select(c => c.Id).SingleAsync();

        var result = await sut.ApplyAsync(correctionId);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
        result.ErrorMessage.Should().Be(PaymentCorrectionService.InvalidStateMessage);
    }
}
