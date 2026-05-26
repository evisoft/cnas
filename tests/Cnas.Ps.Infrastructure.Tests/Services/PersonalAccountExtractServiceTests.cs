using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0516 / TOR CF 02.04 — service-level tests for
/// <see cref="PersonalAccountExtractService"/>. Exercises the per-year /
/// grand-total aggregation, the entry sort order, empty-account semantics,
/// the <c>PersonalAccount.ReadAny</c> permission gate, current-user
/// resolution via the UserProfile→Solicitant identity link, and the audit
/// Sensitive row contract.
/// </summary>
public sealed class PersonalAccountExtractServiceTests
{
    /// <summary>Fixed UTC clock used by every audit-row timestamp.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-personal-account-extract-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Sqid mock — encodes "SQID-{id}".</summary>
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        return sqids;
    }

    /// <summary>Fixed-instant clock substitute.</summary>
    private static ICnasTimeProvider NewClockMock()
    {
        var clock = Substitute.For<ICnasTimeProvider>();
        clock.UtcNow.Returns(ClockNow);
        return clock;
    }

    /// <summary>Audit capture — exposes the most-recent invocation arguments.</summary>
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
    private static ICallerContext NewCaller(long userId, params string[] roles)
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(userId);
        caller.UserSqid.Returns($"USR-{userId}");
        caller.SourceIp.Returns("203.0.113.7");
        caller.CorrelationId.Returns("corr-personal-account");
        caller.Roles.Returns(roles);
        return caller;
    }

    /// <summary>Seeds a UserProfile + Solicitant pair linked via NationalIdHash.</summary>
    private static async Task<(long UserId, long SolicitantId)> SeedUserAndSolicitantAsync(
        CnasDbContext db,
        string idnp = "2000123456789")
    {
        var hash = IdHashHelper.Hash(idnp);
        var user = new UserProfile
        {
            DisplayName = "Ion Popescu",
            NationalId = idnp,
            NationalIdHash = hash,
            Roles = new List<string> { "cnas-user" },
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        var solicitant = new Solicitant
        {
            NationalId = idnp,
            NationalIdHash = hash,
            DisplayName = "Ion Popescu",
            Kind = ApplicantKind.NaturalPerson,
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        db.UserProfiles.Add(user);
        db.Solicitants.Add(solicitant);
        await db.SaveChangesAsync();
        return (user.Id, solicitant.Id);
    }

    /// <summary>Seeds a personal account + entries for the supplied Solicitant.</summary>
    private static async Task<long> SeedPersonalAccountAsync(
        CnasDbContext db,
        long solicitantId,
        string accountCode,
        IEnumerable<(int Year, int Month, decimal Base, decimal Paid, string Source)>? entries = null)
    {
        var account = new PersonalAccount
        {
            OwnerSolicitantId = solicitantId,
            AccountCode = accountCode,
            LifetimeContributions = 0m,
            LifetimeMonths = 0,
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        db.PersonalAccounts.Add(account);
        await db.SaveChangesAsync();

        if (entries is not null)
        {
            foreach (var (year, month, basis, paid, source) in entries)
            {
                db.PersonalAccountEntries.Add(new PersonalAccountEntry
                {
                    PersonalAccountId = account.Id,
                    Year = year,
                    Month = month,
                    ContributionBaseAmount = basis,
                    ContributionPaidAmount = paid,
                    SourceCode = source,
                    CreatedAtUtc = ClockNow.AddDays(-20),
                    IsActive = true,
                });
            }
            await db.SaveChangesAsync();
        }
        return account.Id;
    }

    /// <summary>Builds the SUT around the supplied collaborators.</summary>
    private static PersonalAccountExtractService NewService(
        CnasDbContext db,
        ICallerContext caller,
        IAuditService audit)
        => new(db, NewSqidMock(), caller, NewClockMock(), audit);

    /// <summary>
    /// R0516 / Test 1 — three-year history aggregates per year, grand total
    /// is the sum of all entries.
    /// </summary>
    [Fact]
    public async Task R0516_Extract_ThreeYearHistory_AggregatesPerYear()
    {
        var db = CreateContext();
        var (userId, solicitantId) = await SeedUserAndSolicitantAsync(db);
        await SeedPersonalAccountAsync(db, solicitantId, "PA-1001", new[]
        {
            (2024, 1, 5000m, 1300m, "EMPLOYER_REPORT"),
            (2024, 2, 5000m, 1300m, "EMPLOYER_REPORT"),
            (2025, 6, 6000m, 1560m, "EMPLOYER_REPORT"),
            (2026, 3, 7000m, 1820m, "EMPLOYER_REPORT"),
        });
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        var result = await sut.GetForCurrentUserAsync();

        result.IsSuccess.Should().BeTrue();
        var dto = result.Value;
        // Years DESC: 2026, 2025, 2024.
        dto.Years.Should().HaveCount(3);
        dto.Years[0].Year.Should().Be(2026);
        dto.Years[1].Year.Should().Be(2025);
        dto.Years[2].Year.Should().Be(2024);
        // Grand total = 1300 + 1300 + 1560 + 1820 = 5980.
        dto.GrandTotalContributions.Should().Be(5980m);
        dto.GrandTotalMonths.Should().Be(4);
        // 2024 carries two months.
        dto.Years[2].Months.Should().Be(2);
        dto.Years[2].TotalContributionPaid.Should().Be(2600m);
        dto.AccountCodeSqid.Should().Be("PA-1001");
        dto.SolicitantSqid.Should().Be($"SQID-{solicitantId}");
    }

    /// <summary>
    /// R0516 / Test 2 — entries within a year are sorted ASC by Month.
    /// </summary>
    [Fact]
    public async Task R0516_Extract_EntriesWithinYear_SortedAscendingByMonth()
    {
        var db = CreateContext();
        var (userId, solicitantId) = await SeedUserAndSolicitantAsync(db);
        await SeedPersonalAccountAsync(db, solicitantId, "PA-1002", new[]
        {
            (2025, 11, 1000m, 260m, "EMPLOYER_REPORT"),
            (2025, 1, 1000m, 260m, "EMPLOYER_REPORT"),
            (2025, 6, 1000m, 260m, "EMPLOYER_REPORT"),
        });
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        var result = await sut.GetForCurrentUserAsync();

        result.IsSuccess.Should().BeTrue();
        var year = result.Value.Years[0];
        year.Entries.Select(e => e.Month).Should().ContainInOrder(1, 6, 11);
    }

    /// <summary>
    /// R0516 / Test 3 — empty account returns a successful payload with no
    /// years, zero grand total, zero months.
    /// </summary>
    [Fact]
    public async Task R0516_Extract_EmptyAccount_ReturnsEmptyPayloadWithZeroTotals()
    {
        var db = CreateContext();
        var (userId, solicitantId) = await SeedUserAndSolicitantAsync(db);
        await SeedPersonalAccountAsync(db, solicitantId, "PA-EMPTY");
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        var result = await sut.GetForCurrentUserAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Years.Should().BeEmpty();
        result.Value.GrandTotalContributions.Should().Be(0m);
        result.Value.GrandTotalMonths.Should().Be(0);
    }

    /// <summary>
    /// R0516 / Test 4 — GetForSolicitantAsync without
    /// <c>PersonalAccount.ReadAny</c> returns Forbidden.
    /// </summary>
    [Fact]
    public async Task R0516_GetForSolicitant_WithoutReadAnyPermission_ReturnsForbidden()
    {
        var db = CreateContext();
        var (_, solicitantId) = await SeedUserAndSolicitantAsync(db);
        var (audit, _) = NewAuditCapture();
        // Caller is a plain authenticated user with no permissions.
        var sut = NewService(db, NewCaller(userId: 99L, roles: "cnas-user"), audit);

        var result = await sut.GetForSolicitantAsync(solicitantId);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    /// <summary>
    /// R0516 / Test 5 — GetForCurrentUserAsync resolves the caller's own
    /// Solicitant via the existing UserProfile→Solicitant identity link.
    /// </summary>
    [Fact]
    public async Task R0516_GetForCurrentUser_ResolvesSolicitantViaIdentityLink()
    {
        var db = CreateContext();
        var (userId, solicitantId) = await SeedUserAndSolicitantAsync(db);
        await SeedPersonalAccountAsync(db, solicitantId, "PA-LINKED", new[]
        {
            (2026, 5, 7000m, 1820m, "EMPLOYER_REPORT"),
        });
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        var result = await sut.GetForCurrentUserAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.AccountCodeSqid.Should().Be("PA-LINKED");
        result.Value.SolicitantSqid.Should().Be($"SQID-{solicitantId}");
    }

    /// <summary>
    /// R0516 / Test 6 — audit Sensitive row carries the solicitantSqid +
    /// monthsTotal + yearsCount payload.
    /// </summary>
    [Fact]
    public async Task R0516_Extract_AuditRow_IsSensitive_CarriesSolicitantSqidAndCounters()
    {
        var db = CreateContext();
        var (userId, solicitantId) = await SeedUserAndSolicitantAsync(db);
        await SeedPersonalAccountAsync(db, solicitantId, "PA-AUDIT", new[]
        {
            (2025, 1, 1000m, 260m, "EMPLOYER_REPORT"),
            (2025, 2, 1000m, 260m, "EMPLOYER_REPORT"),
            (2026, 3, 1000m, 260m, "EMPLOYER_REPORT"),
        });
        var (audit, lastAudit) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        var result = await sut.GetForCurrentUserAsync();

        result.IsSuccess.Should().BeTrue();
        var captured = lastAudit();
        captured.Should().NotBeNull();
        captured!.Value.Code.Should().Be(PersonalAccountExtractService.AuditEventCode);
        captured.Value.Severity.Should().Be(AuditSeverity.Sensitive);
        captured.Value.Details.Should().NotBeNull();
        using var doc = JsonDocument.Parse(captured.Value.Details!);
        doc.RootElement.GetProperty("solicitantSqid").GetString().Should().Be($"SQID-{solicitantId}");
        doc.RootElement.GetProperty("monthsTotal").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("yearsCount").GetInt32().Should().Be(2);
    }

    /// <summary>
    /// R0516 / Test 7 — caller with no Solicitant link returns NotFound
    /// rather than fabricating an extract.
    /// </summary>
    [Fact]
    public async Task R0516_GetForCurrentUser_NoSolicitantLink_ReturnsNotFound()
    {
        var db = CreateContext();
        // UserProfile with no matching Solicitant.
        db.UserProfiles.Add(new UserProfile
        {
            Id = 0,
            DisplayName = "Orphan",
            NationalId = null,
            NationalIdHash = null,
            Roles = new List<string> { "cnas-user" },
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        });
        await db.SaveChangesAsync();
        var userId = db.UserProfiles.Single().Id;
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        var result = await sut.GetForCurrentUserAsync();

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    /// <summary>
    /// R0516 / Test 8 — admin with <c>PersonalAccount.ReadAny</c> can read
    /// the extract for any Solicitant.
    /// </summary>
    [Fact]
    public async Task R0516_GetForSolicitant_WithReadAnyPermission_ReturnsExtract()
    {
        var db = CreateContext();
        var (_, solicitantId) = await SeedUserAndSolicitantAsync(db);
        await SeedPersonalAccountAsync(db, solicitantId, "PA-ADMIN-READ", new[]
        {
            (2026, 4, 9000m, 2340m, "EMPLOYER_REPORT"),
        });
        var (audit, _) = NewAuditCapture();
        var adminCaller = NewCaller(userId: 999L, roles: PersonalAccountExtractService.ReadAnyPermission);
        var sut = NewService(db, adminCaller, audit);

        var result = await sut.GetForSolicitantAsync(solicitantId);

        result.IsSuccess.Should().BeTrue();
        result.Value.AccountCodeSqid.Should().Be("PA-ADMIN-READ");
    }
}
