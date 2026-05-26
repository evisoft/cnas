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
/// R0913 / TOR BP 2.2-D — service-level tests for
/// <see cref="InsuredPersonAdjustmentService"/>.
/// </summary>
public sealed class InsuredPersonAdjustmentServiceTests
{
    /// <summary>Fixed UTC clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Canonical first-of-month anchor.</summary>
    private static readonly DateOnly Month = new(2026, 3, 1);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-ipa-{Guid.NewGuid():N}")
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
        caller.CorrelationId.Returns("corr-ipa");
        caller.Roles.Returns((IReadOnlyCollection<string>)["cnas-user"]);
        return caller;
    }

    /// <summary>Builds the SUT.</summary>
    private static InsuredPersonAdjustmentService NewService(CnasDbContext db, IAuditService audit) => new(
        db,
        new StubClock(ClockNow),
        NewSqidMock(),
        NewCaller(),
        audit,
        new InsuredPersonContributionAdjustmentInputDtoValidator());

    /// <summary>Seeds a Solicitant + personal account.</summary>
    private static async Task<(long SolicitantId, long PersonalAccountId)> SeedSolicitantAsync(
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
            AccountCode = $"PA-IPA-{seq:D4}",
            LifetimeContributions = 0m,
            LifetimeMonths = 0,
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        db.PersonalAccounts.Add(pa);
        await db.SaveChangesAsync();
        return (s.Id, pa.Id);
    }

    /// <summary>R0913 — happy path persists adjustment + projects PersonalAccountEntry with SourceCode == document code.</summary>
    [Fact]
    public async Task CreateAsync_HappyPath_PersistsAdjustmentAndProjectsEntry()
    {
        var db = CreateContext();
        var (solicitantId, accountId) = await SeedSolicitantAsync(db, "1234567890701", 1);
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.CreateAsync(new InsuredPersonContributionAdjustmentInputDto(
            InsuredPersonSolicitantSqid: $"SQID-{solicitantId}",
            Month: Month,
            AdjustmentAmount: 500m,
            SourceDocumentCode: "CourtDecision",
            SourceDocumentReference: "Case 2026/123"));

        result.IsSuccess.Should().BeTrue();
        result.Value.SourceDocumentCode.Should().Be("CourtDecision");
        (await db.InsuredPersonContributionAdjustments.CountAsync()).Should().Be(1);
        var entry = await db.PersonalAccountEntries.SingleAsync();
        entry.PersonalAccountId.Should().Be(accountId);
        entry.SourceCode.Should().Be("CourtDecision");
        entry.ContributionPaidAmount.Should().Be(500m);
        last()!.Value.Code.Should().Be(InsuredPersonAdjustmentService.AuditCreated);
        last()!.Value.Severity.Should().Be(AuditSeverity.Notice);
    }

    /// <summary>R0913 — unknown source-document code returns ValidationFailed.</summary>
    [Fact]
    public async Task CreateAsync_UnknownSourceDocumentCode_ReturnsValidationFailed()
    {
        var db = CreateContext();
        var (solicitantId, _) = await SeedSolicitantAsync(db, "1234567890801", 1);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.CreateAsync(new InsuredPersonContributionAdjustmentInputDto(
            InsuredPersonSolicitantSqid: $"SQID-{solicitantId}",
            Month: Month,
            AdjustmentAmount: 100m,
            SourceDocumentCode: "TotallyMadeUp"));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        (await db.InsuredPersonContributionAdjustments.CountAsync()).Should().Be(0);
    }

    /// <summary>R0913 — unknown Solicitant returns NotFound.</summary>
    [Fact]
    public async Task CreateAsync_UnknownSolicitant_ReturnsNotFound()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.CreateAsync(new InsuredPersonContributionAdjustmentInputDto(
            InsuredPersonSolicitantSqid: "SQID-9999",
            Month: Month,
            AdjustmentAmount: 100m,
            SourceDocumentCode: "AdminControl"));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }
}
