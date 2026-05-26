using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Declarations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0813 / TOR BP 1.2-D — service-level tests for
/// <see cref="MonthlyContributionCalculator"/>. Exercises the sum semantics
/// (declared vs adjusted-fallback, over/under-payment delta), idempotent
/// upsert behaviour, validator guard, and the
/// <c>CONTRIBUTOR.MONTHLY_CALC.COMPLETED</c> audit emission.
/// </summary>
public sealed class MonthlyContributionCalculatorTests
{
    /// <summary>Fixed UTC clock used across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Canonical first-of-month anchor (April 2026).</summary>
    private static readonly DateOnly Month = new(2026, 4, 1);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-monthly-calc-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Stub clock.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Sqid mock — encodes "SQID-{id}".</summary>
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        return sqids;
    }

    /// <summary>Audit capture — exposes the most-recent invocation arguments.</summary>
    private static (IAuditService Audit, Func<(string Code, AuditSeverity Severity)?> Last)
        NewAuditCapture()
    {
        (string Code, AuditSeverity Severity)? slot = null;
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
                slot = (call.ArgAt<string>(0), call.ArgAt<AuditSeverity>(1));
                return Task.FromResult(Result.Success());
            });
        return (audit, () => slot);
    }

    /// <summary>Caller stub.</summary>
    private static ICallerContext NewCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(1L);
        caller.UserSqid.Returns("USR-1");
        caller.SourceIp.Returns("127.0.0.1");
        caller.CorrelationId.Returns("corr-monthly");
        caller.Roles.Returns((IReadOnlyCollection<string>)["cnas-user"]);
        return caller;
    }

    /// <summary>Seeds an active contributor and returns its bigint id.</summary>
    private static async Task<long> SeedContributorAsync(CnasDbContext db)
    {
        var c = new Contributor
        {
            Idno = "1003600012346",
            IdnoHash = IdHashHelper.Hash("1003600012346"),
            Denumire = "SRL Test",
            CreatedAtUtc = ClockNow.AddDays(-30),
            RegisteredAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        db.Contributors.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    /// <summary>Seeds a single declaration row with the supplied attributes.</summary>
    private static async Task SeedDeclarationAsync(
        CnasDbContext db,
        long contributorId,
        DeclarationKind kind,
        DateOnly month,
        decimal declared,
        decimal? adjusted = null,
        DeclarationStatus status = DeclarationStatus.Received,
        string? reference = null)
    {
        db.Declarations.Add(new Declaration
        {
            ContributorId = contributorId,
            Kind = kind,
            ReportingMonth = month,
            FiledAtUtc = ClockNow.AddDays(-5),
            ReferenceNumber = reference,
            DeclaredContributionAmount = declared,
            AdjustedContributionAmount = adjusted,
            Status = status,
            CreatedAtUtc = ClockNow.AddDays(-5),
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Builds the SUT.</summary>
    private static MonthlyContributionCalculator NewCalculator(CnasDbContext db, IAuditService audit)
        => new(db, db, new StubClock(ClockNow), NewSqidMock(), NewCaller(), audit);

    /// <summary>R0813 — sums DeclaredContributionAmount across non-cancelled rows.</summary>
    [Fact]
    public async Task CalculateAsync_SumsDeclaredAcrossNonCancelled()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        await SeedDeclarationAsync(db, contributorId, DeclarationKind.Sfs, Month, declared: 1000m, reference: "A");
        await SeedDeclarationAsync(db, contributorId, DeclarationKind.BassFour, Month, declared: 250m);
        await SeedDeclarationAsync(db, contributorId, DeclarationKind.Bass, Month, declared: 999m, status: DeclarationStatus.Cancelled);
        var (audit, _) = NewAuditCapture();
        var sut = NewCalculator(db, audit);

        var result = await sut.CalculateAsync(contributorId, Month, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalDeclared.Should().Be(1250m);
        result.Value.DeclarationCount.Should().Be(2);
    }

    /// <summary>R0813 — sums adjusted when present, falling back to declared.</summary>
    [Fact]
    public async Task CalculateAsync_SumsAdjustedWithFallback()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        await SeedDeclarationAsync(db, contributorId, DeclarationKind.Sfs, Month, declared: 1000m, adjusted: 900m, reference: "A");
        await SeedDeclarationAsync(db, contributorId, DeclarationKind.BassFour, Month, declared: 250m); // no adjusted, falls back to declared
        var (audit, _) = NewAuditCapture();
        var sut = NewCalculator(db, audit);

        var result = await sut.CalculateAsync(contributorId, Month, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalDeclared.Should().Be(1250m);
        result.Value.TotalAdjusted.Should().Be(1150m);
    }

    /// <summary>R0813 — Overpayment populated when adjusted &lt; declared.</summary>
    [Fact]
    public async Task CalculateAsync_DetectsOverpayment()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        await SeedDeclarationAsync(db, contributorId, DeclarationKind.Sfs, Month, declared: 1000m, adjusted: 800m, reference: "A");
        var (audit, _) = NewAuditCapture();
        var sut = NewCalculator(db, audit);

        var result = await sut.CalculateAsync(contributorId, Month, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.OverpaymentAmount.Should().Be(200m);
        result.Value.UnderpaymentAmount.Should().BeNull();
    }

    /// <summary>R0813 — Underpayment populated when adjusted &gt; declared.</summary>
    [Fact]
    public async Task CalculateAsync_DetectsUnderpayment()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        await SeedDeclarationAsync(db, contributorId, DeclarationKind.Sfs, Month, declared: 1000m, adjusted: 1200m, reference: "A");
        var (audit, _) = NewAuditCapture();
        var sut = NewCalculator(db, audit);

        var result = await sut.CalculateAsync(contributorId, Month, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.UnderpaymentAmount.Should().Be(200m);
        result.Value.OverpaymentAmount.Should().BeNull();
    }

    /// <summary>R0813 — re-running for the same (contributor, month) upserts in place.</summary>
    [Fact]
    public async Task CalculateAsync_RerunUpsertsInPlace()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        await SeedDeclarationAsync(db, contributorId, DeclarationKind.Sfs, Month, declared: 500m, reference: "A");
        var (audit, _) = NewAuditCapture();
        var sut = NewCalculator(db, audit);

        var first = await sut.CalculateAsync(contributorId, Month, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        await SeedDeclarationAsync(db, contributorId, DeclarationKind.BassFour, Month, declared: 100m);
        var second = await sut.CalculateAsync(contributorId, Month, CancellationToken.None);
        second.IsSuccess.Should().BeTrue();

        var rows = await db.MonthlyContributionCalculations
            .Where(r => r.ContributorId == contributorId && r.Month == Month)
            .ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].TotalDeclared.Should().Be(600m);
        rows[0].DeclarationCount.Should().Be(2);
    }

    /// <summary>R0813 — emits the documented audit event.</summary>
    [Fact]
    public async Task CalculateAsync_AuditsCompletedEvent()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        await SeedDeclarationAsync(db, contributorId, DeclarationKind.Sfs, Month, declared: 1m, reference: "A");
        var (audit, last) = NewAuditCapture();
        var sut = NewCalculator(db, audit);

        var result = await sut.CalculateAsync(contributorId, Month, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        last()!.Value.Code.Should().Be(MonthlyContributionCalculator.AuditCompleted);
        last()!.Value.Severity.Should().Be(AuditSeverity.Information);
    }

    /// <summary>R0813 — non-first-of-month input is rejected.</summary>
    [Fact]
    public async Task CalculateAsync_NonFirstOfMonth_Fails()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewCalculator(db, audit);

        var result = await sut.CalculateAsync(contributorId, new DateOnly(2026, 4, 15), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }
}
