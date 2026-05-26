using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Rev5;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0910 / R0913 — service-level tests for the REV-5 declarations registry
/// and per-insured-person contribution-adjustment paths.
/// </summary>
public sealed class Rev5DeclarationServiceTests
{
    /// <summary>Fixed UTC clock used by every test (2026-05-22 12:00 UTC).</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Canonical first-of-month anchor (April 2026 — distinct from the clock month).</summary>
    private static readonly DateOnly ReportingMonth = new(2026, 4, 1);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-rev5-{Guid.NewGuid():N}")
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
        caller.CorrelationId.Returns("corr-rev5");
        caller.Roles.Returns((IReadOnlyCollection<string>)["cnas-user"]);
        return caller;
    }

    /// <summary>Stub <see cref="Cnas.Ps.Application.ManagementPeriods.IManagementPeriodService"/> with every month open by default.</summary>
    private static Cnas.Ps.Application.ManagementPeriods.IManagementPeriodService NewOpenPeriods()
    {
        var periods = Substitute.For<Cnas.Ps.Application.ManagementPeriods.IManagementPeriodService>();
        periods.IsMonthClosedAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        return periods;
    }

    /// <summary>Builds the SUT with default open-month policy.</summary>
    private static Rev5DeclarationService NewService(
        CnasDbContext db,
        IAuditService audit,
        Cnas.Ps.Application.ManagementPeriods.IManagementPeriodService? periods = null)
        => new(
            db,
            new StubClock(ClockNow),
            NewSqidMock(),
            NewCaller(),
            audit,
            periods ?? NewOpenPeriods(),
            new Rev5DeclarationRegisterInputDtoValidator(),
            new Rev5DeclarationRowAdjustInputDtoValidator(),
            new Rev5DeclarationCancelInputDtoValidator());

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

    // ───────── R0910 — RegisterAsync ─────────

    /// <summary>R0910 — happy path persists header + rows and projects PersonalAccountEntry for each resolved hash.</summary>
    [Fact]
    public async Task RegisterAsync_HappyPath_PersistsHeaderRowsAndProjectsEntries()
    {
        var db = CreateContext();
        var employerId = await SeedContributorAsync(db);
        var (_, accountA, hashA) = await SeedSolicitantWithAccountAsync(db, "1234567890101", 1);
        var (_, accountB, hashB) = await SeedSolicitantWithAccountAsync(db, "1234567890102", 2);
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.RegisterAsync(new Rev5DeclarationRegisterInputDto(
            FilingContributorSqid: $"SQID-{employerId}",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "REV5-001",
            Rows: new[]
            {
                new Rev5DeclarationRowInputDto(hashA, 10_000m, 2_900m),
                new Rev5DeclarationRowInputDto(hashB, 8_000m, 2_320m),
            }));

        result.IsSuccess.Should().BeTrue();
        result.Value.RowCount.Should().Be(2);
        result.Value.UnmatchedRowCount.Should().Be(0);
        result.Value.TotalDeclaredAmount.Should().Be(5_220m);

        (await db.Rev5DeclarationRows.CountAsync()).Should().Be(2);
        (await db.PersonalAccountEntries.CountAsync(e => e.PersonalAccountId == accountA && e.SourceCode == "REV5")).Should().Be(1);
        (await db.PersonalAccountEntries.CountAsync(e => e.PersonalAccountId == accountB && e.SourceCode == "REV5")).Should().Be(1);
        last()!.Value.Code.Should().Be(Rev5DeclarationService.AuditRegistered);
        last()!.Value.Severity.Should().Be(AuditSeverity.Information);
        last()!.Value.Details.Should().Contain("\"unmatchedCount\":0");
    }

    /// <summary>R0910 — partial-match: 3 rows, 1 unmatched IDNP hash → UnmatchedRowCount=1.</summary>
    [Fact]
    public async Task RegisterAsync_PartialMatch_FlagsUnmatchedRows()
    {
        var db = CreateContext();
        var employerId = await SeedContributorAsync(db);
        var (_, _, hashA) = await SeedSolicitantWithAccountAsync(db, "1234567890201", 1);
        var (_, _, hashB) = await SeedSolicitantWithAccountAsync(db, "1234567890202", 2);
        const string unknownHash = "AAAAAAAAUNKNOWN_HASH_XYZ";
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.RegisterAsync(new Rev5DeclarationRegisterInputDto(
            FilingContributorSqid: $"SQID-{employerId}",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "REV5-PARTIAL",
            Rows: new[]
            {
                new Rev5DeclarationRowInputDto(hashA, 5_000m, 1_450m),
                new Rev5DeclarationRowInputDto(unknownHash, 3_000m, 870m),
                new Rev5DeclarationRowInputDto(hashB, 4_000m, 1_160m),
            }));

        result.IsSuccess.Should().BeTrue();
        result.Value.UnmatchedRowCount.Should().Be(1);
        result.Value.UnmatchedNationalIdHashPrefixes.Should().HaveCount(1);
        result.Value.UnmatchedNationalIdHashPrefixes[0].Should().Be(unknownHash[..8]);
        (await db.Rev5DeclarationRows.CountAsync()).Should().Be(3);
        (await db.PersonalAccountEntries.CountAsync()).Should().Be(2);
    }

    /// <summary>R0910 — closed reporting month is refused with MONTH_CLOSED.</summary>
    [Fact]
    public async Task RegisterAsync_ClosedMonth_RefusesWithMonthClosed()
    {
        var db = CreateContext();
        var employerId = await SeedContributorAsync(db);
        var (_, _, hash) = await SeedSolicitantWithAccountAsync(db, "1234567890301", 1);
        var (audit, _) = NewAuditCapture();
        var closedPeriods = Substitute.For<Cnas.Ps.Application.ManagementPeriods.IManagementPeriodService>();
        closedPeriods
            .IsMonthClosedAsync(ReportingMonth, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        var sut = NewService(db, audit, closedPeriods);

        var result = await sut.RegisterAsync(new Rev5DeclarationRegisterInputDto(
            FilingContributorSqid: $"SQID-{employerId}",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "REV5-CLOSED",
            Rows: [new Rev5DeclarationRowInputDto(hash, 1_000m, 290m)]));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Be(Rev5DeclarationService.MonthClosedMessage);
        (await db.Rev5Declarations.CountAsync()).Should().Be(0);
    }

    /// <summary>R0910 — duplicate natural key returns Conflict with REV5_DUPLICATE.</summary>
    [Fact]
    public async Task RegisterAsync_DuplicateNaturalKey_ReturnsConflict()
    {
        var db = CreateContext();
        var employerId = await SeedContributorAsync(db);
        var (_, _, hash) = await SeedSolicitantWithAccountAsync(db, "1234567890401", 1);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var first = await sut.RegisterAsync(new Rev5DeclarationRegisterInputDto(
            FilingContributorSqid: $"SQID-{employerId}",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "REV5-DUP",
            Rows: [new Rev5DeclarationRowInputDto(hash, 1_000m, 290m)]));
        first.IsSuccess.Should().BeTrue();

        var dup = await sut.RegisterAsync(new Rev5DeclarationRegisterInputDto(
            FilingContributorSqid: $"SQID-{employerId}",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "REV5-DUP",
            Rows: [new Rev5DeclarationRowInputDto(hash, 1_000m, 290m)]));

        dup.IsFailure.Should().BeTrue();
        dup.ErrorCode.Should().Be(ErrorCodes.Conflict);
        dup.ErrorMessage.Should().Be(Rev5DeclarationService.DuplicateMessage);
    }

    // ───────── R0910 — AdjustRowAsync ─────────

    /// <summary>R0910 — AdjustRowAsync updates row + re-projects PersonalAccountEntry.</summary>
    [Fact]
    public async Task AdjustRowAsync_HappyPath_UpdatesRowAndReprojectsEntry()
    {
        var db = CreateContext();
        var employerId = await SeedContributorAsync(db);
        var (_, accountA, hash) = await SeedSolicitantWithAccountAsync(db, "1234567890501", 1);
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);

        var registered = await sut.RegisterAsync(new Rev5DeclarationRegisterInputDto(
            FilingContributorSqid: $"SQID-{employerId}",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "REV5-ADJ",
            Rows: [new Rev5DeclarationRowInputDto(hash, 5_000m, 1_450m)]));
        registered.IsSuccess.Should().BeTrue();
        var header = await db.Rev5Declarations.SingleAsync();

        var result = await sut.AdjustRowAsync(header.Id, hash, 1_500m, "Operator correction", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(Rev5DeclarationStatus.Adjusted));
        var entry = await db.PersonalAccountEntries
            .SingleAsync(e => e.PersonalAccountId == accountA && e.SourceCode == "REV5");
        entry.ContributionPaidAmount.Should().Be(1_500m);
        last()!.Value.Code.Should().Be(Rev5DeclarationService.AuditAdjusted);
        last()!.Value.Severity.Should().Be(AuditSeverity.Notice);
    }

    // ───────── R0910 — CancelAsync ─────────

    /// <summary>R0910 — CancelAsync rolls back PersonalAccountEntry rows + writes Critical audit.</summary>
    [Fact]
    public async Task CancelAsync_HappyPath_RollsBackEntriesAndAuditsCritical()
    {
        var db = CreateContext();
        var employerId = await SeedContributorAsync(db);
        var (_, accountA, hashA) = await SeedSolicitantWithAccountAsync(db, "1234567890601", 1);
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);

        var registered = await sut.RegisterAsync(new Rev5DeclarationRegisterInputDto(
            FilingContributorSqid: $"SQID-{employerId}",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "REV5-CANCEL",
            Rows: [new Rev5DeclarationRowInputDto(hashA, 1_000m, 290m)]));
        registered.IsSuccess.Should().BeTrue();
        var header = await db.Rev5Declarations.SingleAsync();
        (await db.PersonalAccountEntries.CountAsync(e => e.PersonalAccountId == accountA && e.IsActive)).Should().Be(1);

        var result = await sut.CancelAsync(header.Id, "Operator cancel rationale", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await db.Rev5Declarations.SingleAsync(d => d.Id == header.Id);
        reloaded.Status.Should().Be(Rev5DeclarationStatus.Cancelled);
        (await db.PersonalAccountEntries.CountAsync(e => e.PersonalAccountId == accountA && e.IsActive)).Should().Be(0);
        last()!.Value.Code.Should().Be(Rev5DeclarationService.AuditCancelled);
        last()!.Value.Severity.Should().Be(AuditSeverity.Critical);
    }
}
