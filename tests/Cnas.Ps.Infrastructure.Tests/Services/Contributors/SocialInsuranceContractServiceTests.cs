using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Contributors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services.Contributors;

/// <summary>
/// R0912 / TOR BP 2.2-C — service-level tests for the social-insurance
/// contract lifecycle (issue / modify / terminate). Covers the supersession
/// invariant, the overlapping-contract guard, and the audit-event shape.
/// </summary>
public sealed class SocialInsuranceContractServiceTests
{
    /// <summary>Fixed UTC clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-sic-{Guid.NewGuid():N}")
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
        caller.CorrelationId.Returns("corr-sic");
        caller.Roles.Returns((IReadOnlyCollection<string>)["cnas-user"]);
        return caller;
    }

    /// <summary>Builds the SUT.</summary>
    private static SocialInsuranceContractService NewService(CnasDbContext db, IAuditService audit)
        => new(
            db,
            new StubClock(ClockNow),
            NewSqidMock(),
            NewCaller(),
            audit,
            new SocialInsuranceContractIssueDtoValidator(),
            new SocialInsuranceContractModifyDtoValidator(),
            new SocialInsuranceContractTerminateDtoValidator());

    /// <summary>Seeds an InsuredPerson — modelled as "Contributor" in the BP 2.2-C language.</summary>
    private static async Task<long> SeedInsuredPersonAsync(CnasDbContext db, bool active = true)
    {
        var idnp = "0123456789012";
        var p = new InsuredPerson
        {
            Idnp = idnp,
            IdnpHash = IdHashHelper.Hash(idnp),
            LastName = "DOE",
            FirstName = "Jane",
            BirthDate = new DateOnly(1985, 1, 1),
            RegisteredAtUtc = ClockNow.AddDays(-30),
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = active,
        };
        db.InsuredPersons.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    // ───────── R0912 — IssueAsync ─────────

    /// <summary>R0912 — IssueAsync persists a fresh contract + Critical audit.</summary>
    [Fact]
    public async Task IssueAsync_HappyPath_PersistsContractAndAuditsCritical()
    {
        var db = CreateContext();
        var contributorId = await SeedInsuredPersonAsync(db);
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.IssueAsync(new SocialInsuranceContractIssueDto(
            ContributorSqid: $"SQID-{contributorId}",
            ContractNumber: "SIC-2026-0001",
            ContractStartDate: new DateOnly(2026, 5, 1),
            ContractEndDate: null,
            MonthlyContributionAmount: 1_000m,
            CounterpartyName: "CNAS Chișinău",
            ChangeReason: "Voluntary social-insurance contract — initial issue"));

        result.IsSuccess.Should().BeTrue();
        result.Value.ContractNumber.Should().Be("SIC-2026-0001");
        (await db.ContributorSocialInsuranceContracts.CountAsync()).Should().Be(1);
        last()!.Value.Code.Should().Be(SocialInsuranceContractService.AuditIssued);
        last()!.Value.Severity.Should().Be(AuditSeverity.Critical);
    }

    /// <summary>R0912 — IssueAsync rejects deactivated InsuredPerson.</summary>
    [Fact]
    public async Task IssueAsync_DeactivatedContributor_ReturnsNotFound()
    {
        var db = CreateContext();
        var contributorId = await SeedInsuredPersonAsync(db, active: false);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.IssueAsync(new SocialInsuranceContractIssueDto(
            $"SQID-{contributorId}", "SIC-X", new DateOnly(2026, 5, 1), null, 100m, null,
            "Voluntary social-insurance contract — initial issue"));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    /// <summary>R0912 — IssueAsync rejects overlapping current contract.</summary>
    [Fact]
    public async Task IssueAsync_OverlappingActive_ReturnsConflict()
    {
        var db = CreateContext();
        var contributorId = await SeedInsuredPersonAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var first = await sut.IssueAsync(new SocialInsuranceContractIssueDto(
            $"SQID-{contributorId}", "SIC-1", new DateOnly(2026, 5, 1), null, 100m, null,
            "Initial voluntary social-insurance contract"));
        first.IsSuccess.Should().BeTrue();

        var dup = await sut.IssueAsync(new SocialInsuranceContractIssueDto(
            $"SQID-{contributorId}", "SIC-2", new DateOnly(2026, 5, 2), null, 200m, null,
            "Should be rejected — overlapping active"));

        dup.IsFailure.Should().BeTrue();
        dup.ErrorCode.Should().Be(ErrorCodes.Conflict);
        dup.ErrorMessage.Should().Be(SocialInsuranceContractService.OverlappingContractMessage);
    }

    // ───────── R0912 — ModifyAsync ─────────

    /// <summary>R0912 — ModifyAsync supersedes current contract (flip ValidToUtc + new row) + audit Critical.</summary>
    [Fact]
    public async Task ModifyAsync_HappyPath_SupersedesAndAuditsCritical()
    {
        var db = CreateContext();
        var contributorId = await SeedInsuredPersonAsync(db);
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);
        var issued = await sut.IssueAsync(new SocialInsuranceContractIssueDto(
            $"SQID-{contributorId}", "SIC-INIT", new DateOnly(2026, 5, 1), null, 100m, null,
            "Initial voluntary social-insurance contract"));
        issued.IsSuccess.Should().BeTrue();
        var initial = await db.ContributorSocialInsuranceContracts.SingleAsync();

        var result = await sut.ModifyAsync(initial.Id, new SocialInsuranceContractModifyDto(
            ContractNumber: "SIC-MODIFIED",
            ContractStartDate: null,
            ContractEndDate: null,
            MonthlyContributionAmount: 500m,
            CounterpartyName: null,
            ChangeReason: "Monthly contribution increased per operator request"));

        result.IsSuccess.Should().BeTrue();
        var rows = await db.ContributorSocialInsuranceContracts
            .Where(c => c.ContributorId == contributorId)
            .OrderBy(c => c.Id)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].ValidToUtc.Should().Be(ClockNow);
        rows[1].ValidToUtc.Should().BeNull();
        rows[1].MonthlyContributionAmount.Should().Be(500m);
        last()!.Value.Code.Should().Be(SocialInsuranceContractService.AuditModified);
        last()!.Value.Severity.Should().Be(AuditSeverity.Critical);
    }

    // ───────── R0912 — TerminateAsync ─────────

    /// <summary>R0912 — TerminateAsync sets EndDate + ValidToUtc + audit Critical.</summary>
    [Fact]
    public async Task TerminateAsync_HappyPath_SetsEndDateAndAuditsCritical()
    {
        var db = CreateContext();
        var contributorId = await SeedInsuredPersonAsync(db);
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);
        var issued = await sut.IssueAsync(new SocialInsuranceContractIssueDto(
            $"SQID-{contributorId}", "SIC-TERM", new DateOnly(2026, 5, 1), null, 100m, null,
            "Initial voluntary social-insurance contract"));
        issued.IsSuccess.Should().BeTrue();
        var contract = await db.ContributorSocialInsuranceContracts.SingleAsync();

        var result = await sut.TerminateAsync(
            contract.Id,
            new DateOnly(2026, 6, 30),
            "Citizen requested termination",
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await db.ContributorSocialInsuranceContracts.SingleAsync(c => c.Id == contract.Id);
        reloaded.ContractEndDate.Should().Be(new DateOnly(2026, 6, 30));
        reloaded.ValidToUtc.Should().Be(ClockNow);
        last()!.Value.Code.Should().Be(SocialInsuranceContractService.AuditTerminated);
        last()!.Value.Severity.Should().Be(AuditSeverity.Critical);
    }
}
