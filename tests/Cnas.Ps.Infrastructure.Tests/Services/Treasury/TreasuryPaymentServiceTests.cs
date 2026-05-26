using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Treasury;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services.Treasury;

/// <summary>
/// R0911 / TOR BP 2.2-B — service-level tests for the Treasury
/// payment-receipt registry and per-receipt distribution. Covers
/// happy / no-Rev5 / partial / duplicate / already-distributed paths.
/// </summary>
public sealed class TreasuryPaymentServiceTests
{
    /// <summary>Fixed UTC clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Canonical reporting month anchored on the first of the month.</summary>
    private static readonly DateOnly ReportingMonth = new(2026, 4, 1);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-treasury-{Guid.NewGuid():N}")
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

    /// <summary>Captures the last audit invocation for assertion.</summary>
    private static (IAuditService Audit, Func<(string Code, AuditSeverity Severity, string? Details, long? TargetId)?> Last)
        NewAuditCapture()
    {
        (string Code, AuditSeverity Severity, string? Details, long? TargetId)? slot = null;
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
                slot = (
                    call.ArgAt<string>(0),
                    call.ArgAt<AuditSeverity>(1),
                    call.ArgAt<string>(5),
                    call.ArgAt<long?>(4));
                return Task.FromResult(Result.Success());
            });
        return (audit, () => slot);
    }

    /// <summary>Authenticated-caller helper.</summary>
    private static ICallerContext NewCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(1L);
        caller.UserSqid.Returns("USR-1");
        caller.SourceIp.Returns("203.0.113.7");
        caller.CorrelationId.Returns("corr-treasury");
        caller.Roles.Returns((IReadOnlyCollection<string>)["cnas-user"]);
        return caller;
    }

    /// <summary>Builds the SUT.</summary>
    private static TreasuryPaymentService NewService(CnasDbContext db, IAuditService audit)
        => new(
            db,
            new StubClock(ClockNow),
            NewSqidMock(),
            NewCaller(),
            audit,
            new TreasuryPaymentReceiptImportInputDtoValidator());

    /// <summary>Seeds an active employer (Plătitor).</summary>
    private static async Task<long> SeedContributorAsync(CnasDbContext db)
    {
        var c = new Contributor
        {
            Idno = "1003600099991",
            IdnoHash = IdHashHelper.Hash("1003600099991"),
            Denumire = "SRL Employer",
            CreatedAtUtc = ClockNow.AddDays(-30),
            RegisteredAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        db.Contributors.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    /// <summary>Seeds a Solicitant with the given IDNP and an attached personal account.</summary>
    private static async Task<(long SolicitantId, long PersonalAccountId, string Hash)> SeedSolicitantWithAccountAsync(
        CnasDbContext db,
        string idnp,
        int seq)
    {
        var hash = IdHashHelper.Hash(idnp);
        var s = new Solicitant
        {
            NationalId = idnp,
            NationalIdHash = hash,
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = $"Employee {seq}",
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        db.Solicitants.Add(s);
        await db.SaveChangesAsync();

        var pa = new PersonalAccount
        {
            OwnerSolicitantId = s.Id,
            AccountCode = $"PA-{seq:D4}",
            LifetimeContributions = 0m,
            LifetimeMonths = 0,
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        db.PersonalAccounts.Add(pa);
        await db.SaveChangesAsync();
        return (s.Id, pa.Id, hash);
    }

    /// <summary>Seeds a Rev5 declaration header + rows for the given payer × month.</summary>
    private static async Task<long> SeedRev5DeclarationAsync(
        CnasDbContext db,
        long employerId,
        DateOnly month,
        string referenceNumber,
        params (string Hash, decimal Base, decimal Contribution)[] rows)
    {
        var header = new Rev5Declaration
        {
            FilingContributorId = employerId,
            ReportingMonth = month,
            ReferenceNumber = referenceNumber,
            Status = Rev5DeclarationStatus.Validated,
            TotalDeclaredAmount = rows.Sum(r => r.Contribution),
            RowCount = rows.Length,
            FiledAtUtc = ClockNow.AddHours(-1),
            CreatedAtUtc = ClockNow.AddHours(-1),
            IsActive = true,
        };
        db.Rev5Declarations.Add(header);
        await db.SaveChangesAsync();
        foreach (var (hash, baseAmount, contribution) in rows)
        {
            db.Rev5DeclarationRows.Add(new Rev5DeclarationRow
            {
                Rev5DeclarationId = header.Id,
                InsuredPersonNationalIdHash = hash,
                ContributionBaseAmount = baseAmount,
                ContributionAmount = contribution,
                CreatedAtUtc = ClockNow.AddHours(-1),
                IsActive = true,
            });
        }
        await db.SaveChangesAsync();
        return header.Id;
    }

    // ───────── R0911 — ImportReceiptAsync ─────────

    /// <summary>R0911 — ImportReceiptAsync persists Pending row + audit Information.</summary>
    [Fact]
    public async Task ImportReceiptAsync_HappyPath_PersistsPendingAndAuditsInformation()
    {
        var db = CreateContext();
        var employerId = await SeedContributorAsync(db);
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.ImportReceiptAsync(new TreasuryPaymentReceiptImportInputDto(
            TreasuryReferenceNumber: "TRS-0001",
            ReceiptDate: new DateOnly(2026, 5, 10),
            PayerContributorSqid: $"SQID-{employerId}",
            ReportingMonth: ReportingMonth,
            AmountReceived: 5_000m));

        result.IsSuccess.Should().BeTrue();
        result.Value.DistributionStatus.Should().Be(nameof(TreasuryPaymentDistributionStatus.Pending));
        (await db.TreasuryPaymentReceipts.CountAsync()).Should().Be(1);
        last()!.Value.Code.Should().Be(TreasuryPaymentService.AuditImported);
        last()!.Value.Severity.Should().Be(AuditSeverity.Information);
    }

    /// <summary>R0911 — ImportReceiptAsync rejects duplicate Treasury reference.</summary>
    [Fact]
    public async Task ImportReceiptAsync_DuplicateReference_RejectsWithStableMessage()
    {
        var db = CreateContext();
        var employerId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var first = await sut.ImportReceiptAsync(new TreasuryPaymentReceiptImportInputDto(
            "TRS-DUP", new DateOnly(2026, 5, 10), $"SQID-{employerId}", ReportingMonth, 1_000m));
        first.IsSuccess.Should().BeTrue();

        var dup = await sut.ImportReceiptAsync(new TreasuryPaymentReceiptImportInputDto(
            "TRS-DUP", new DateOnly(2026, 5, 11), $"SQID-{employerId}", ReportingMonth, 1_000m));

        dup.IsFailure.Should().BeTrue();
        dup.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        dup.ErrorMessage.Should().Be(TreasuryPaymentService.DuplicateMessage);
    }

    /// <summary>R0911 — validator rejects AmountReceived of 0.</summary>
    [Fact]
    public async Task ImportReceiptAsync_ZeroAmount_RejectsValidation()
    {
        var db = CreateContext();
        var employerId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var res = await sut.ImportReceiptAsync(new TreasuryPaymentReceiptImportInputDto(
            "TRS-ZERO", new DateOnly(2026, 5, 10), $"SQID-{employerId}", ReportingMonth, 0m));

        res.IsFailure.Should().BeTrue();
        res.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ───────── R0911 — DistributeAsync ─────────

    /// <summary>R0911 — DistributeAsync happy-path distributes full amount proportionally.</summary>
    [Fact]
    public async Task DistributeAsync_HappyPath_DistributesProportionally()
    {
        var db = CreateContext();
        var employerId = await SeedContributorAsync(db);
        var (_, accountA, hashA) = await SeedSolicitantWithAccountAsync(db, "1234567890101", 1);
        var (_, accountB, hashB) = await SeedSolicitantWithAccountAsync(db, "1234567890102", 2);
        await SeedRev5DeclarationAsync(db, employerId, ReportingMonth, "REV-D1",
            (hashA, 5_000m, 100m),
            (hashB, 10_000m, 200m));
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);

        var import = await sut.ImportReceiptAsync(new TreasuryPaymentReceiptImportInputDto(
            "TRS-OK1", new DateOnly(2026, 5, 12), $"SQID-{employerId}", ReportingMonth, 300m));
        import.IsSuccess.Should().BeTrue();
        var receiptId = await db.TreasuryPaymentReceipts.Select(r => r.Id).SingleAsync();

        var result = await sut.DistributeAsync(receiptId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DistributionStatus.Should().Be(nameof(TreasuryPaymentDistributionStatus.Distributed));
        var entryA = await db.PersonalAccountEntries
            .SingleAsync(e => e.PersonalAccountId == accountA && e.SourceCode == "TREASURY");
        var entryB = await db.PersonalAccountEntries
            .SingleAsync(e => e.PersonalAccountId == accountB && e.SourceCode == "TREASURY");
        entryA.ContributionPaidAmount.Should().Be(100m);
        entryB.ContributionPaidAmount.Should().Be(200m);
        last()!.Value.Code.Should().Be(TreasuryPaymentService.AuditDistributed);
        last()!.Value.Severity.Should().Be(AuditSeverity.Critical);
    }

    /// <summary>R0911 — DistributeAsync with no matching REV-5 flips to Failed with full remainder.</summary>
    [Fact]
    public async Task DistributeAsync_NoMatchingRev5_FlagsFailedWithFullRemainder()
    {
        var db = CreateContext();
        var employerId = await SeedContributorAsync(db);
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);
        var import = await sut.ImportReceiptAsync(new TreasuryPaymentReceiptImportInputDto(
            "TRS-NOREV5", new DateOnly(2026, 5, 12), $"SQID-{employerId}", ReportingMonth, 1_500m));
        import.IsSuccess.Should().BeTrue();
        var receiptId = await db.TreasuryPaymentReceipts.Select(r => r.Id).SingleAsync();

        var result = await sut.DistributeAsync(receiptId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DistributionStatus.Should().Be(nameof(TreasuryPaymentDistributionStatus.Failed));
        result.Value.DistributionFailureReason.Should().Be(TreasuryPaymentService.NoRev5ToDistributeMessage);
        result.Value.UndistributedRemainderAmount.Should().Be(1_500m);
        last()!.Value.Severity.Should().Be(AuditSeverity.Critical);
    }

    /// <summary>R0911 — DistributeAsync partial: one row missing personal account → PartiallyDistributed.</summary>
    [Fact]
    public async Task DistributeAsync_PartialMatch_FlagsPartiallyDistributedWithRemainder()
    {
        var db = CreateContext();
        var employerId = await SeedContributorAsync(db);
        var (_, _, hashA) = await SeedSolicitantWithAccountAsync(db, "1234567890201", 1);
        // hashB has no Solicitant — falls through to remainder.
        var hashB = IdHashHelper.Hash("1234567890299");
        await SeedRev5DeclarationAsync(db, employerId, ReportingMonth, "REV-PART",
            (hashA, 5_000m, 100m),
            (hashB, 5_000m, 100m));
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var import = await sut.ImportReceiptAsync(new TreasuryPaymentReceiptImportInputDto(
            "TRS-PART", new DateOnly(2026, 5, 12), $"SQID-{employerId}", ReportingMonth, 200m));
        import.IsSuccess.Should().BeTrue();
        var receiptId = await db.TreasuryPaymentReceipts.Select(r => r.Id).SingleAsync();

        var result = await sut.DistributeAsync(receiptId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DistributionStatus.Should().Be(nameof(TreasuryPaymentDistributionStatus.PartiallyDistributed));
        result.Value.UndistributedRemainderAmount.Should().Be(100m);
    }

    /// <summary>R0911 — DistributeAsync rejects already-Distributed receipts.</summary>
    [Fact]
    public async Task DistributeAsync_AlreadyDistributed_ReturnsStableFailure()
    {
        var db = CreateContext();
        var employerId = await SeedContributorAsync(db);
        var (_, _, hash) = await SeedSolicitantWithAccountAsync(db, "1234567890401", 1);
        await SeedRev5DeclarationAsync(db, employerId, ReportingMonth, "REV-DUP",
            (hash, 5_000m, 100m));
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var import = await sut.ImportReceiptAsync(new TreasuryPaymentReceiptImportInputDto(
            "TRS-IDEMP", new DateOnly(2026, 5, 12), $"SQID-{employerId}", ReportingMonth, 100m));
        import.IsSuccess.Should().BeTrue();
        var receiptId = await db.TreasuryPaymentReceipts.Select(r => r.Id).SingleAsync();
        var first = await sut.DistributeAsync(receiptId, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        var second = await sut.DistributeAsync(receiptId, CancellationToken.None);

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        second.ErrorMessage.Should().Be(TreasuryPaymentService.AlreadyDistributedMessage);
    }

    /// <summary>R0911 — DistributeAsync NotFound for unknown receipt.</summary>
    [Fact]
    public async Task DistributeAsync_UnknownReceipt_ReturnsNotFound()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.DistributeAsync(receiptId: 99_999L, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }
}
