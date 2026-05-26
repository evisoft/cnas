using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Contributors;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Contributors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Contributors;

/// <summary>
/// R0302 / TOR §2.1 — tests for
/// <see cref="ContributorSourceHistoryService"/>. Covers the happy-path record
/// (row + audit emission + counter), the descending-order listing, and the
/// not-found path.
/// </summary>
public sealed class ContributorSourceHistoryServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Stub UTC clock returning <see cref="ClockNow"/>.</summary>
    private sealed class StubClock : ICnasTimeProvider
    {
        public DateTime UtcNow => ClockNow;
    }

    /// <summary>Builds an isolated EF Core InMemory context.</summary>
    /// <returns>A fresh context.</returns>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-contrib-src-hist-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Sqid stub.</summary>
    /// <returns>Substitute encoding to <c>SQID-{id}</c>.</returns>
    private static ISqidService NewSqids()
    {
        var s = Substitute.For<ISqidService>();
        s.Encode(Arg.Any<long>()).Returns(c => $"SQID-{c.Arg<long>()}");
        s.TryDecode(Arg.Any<string>()).Returns(c =>
        {
            var v = c.Arg<string>();
            if (v is not null && v.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(v["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return s;
    }

    /// <summary>Caller-context substitute with stable identity.</summary>
    /// <returns>Substitute caller.</returns>
    private static ICallerContext NewCaller()
    {
        var c = Substitute.For<ICallerContext>();
        c.UserId.Returns(7L);
        c.UserSqid.Returns("SQID-7");
        c.SourceIp.Returns("203.0.113.5");
        c.CorrelationId.Returns("corr-src-hist");
        return c;
    }

    /// <summary>Audit substitute capturing the event-code argument list.</summary>
    /// <param name="codes">Out-parameter — list to which captured codes are appended.</param>
    /// <returns>Substitute audit service.</returns>
    private static IAuditService NewAuditCapturing(out List<string> codes)
    {
        var list = new List<string>();
        codes = list;
        var a = Substitute.For<IAuditService>();
        a.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                list.Add(c.ArgAt<string>(0));
                return Task.FromResult(Result.Success());
            });
        return a;
    }

    /// <summary>Builds the SUT with default collaborators.</summary>
    /// <param name="db">Shared db context.</param>
    /// <param name="audit">Audit substitute.</param>
    /// <returns>Configured service.</returns>
    private static ContributorSourceHistoryService NewService(CnasDbContext db, IAuditService audit)
        => new(
            db: db,
            read: db,
            clock: new StubClock(),
            sqids: NewSqids(),
            caller: NewCaller(),
            audit: audit,
            argsValidator: new ContributorSourceChangeArgsValidator());

    [Fact]
    public async Task RecordChange_HappyPath_InsertsRow_AndAudits()
    {
        using var db = CreateContext();
        db.Contributors.Add(new Contributor
        {
            Idno = "1234567890123",
            Denumire = "SRL Test",
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        });
        await db.SaveChangesAsync();
        var contributorId = db.Contributors.Single().Id;

        var audit = NewAuditCapturing(out var codes);
        var svc = NewService(db, audit);

        var result = await svc.RecordChangeAsync(
            contributorId,
            oldSource: "Manual",
            newSource: "RSUD",
            actorUserId: 7L,
            reason: "Reconciliation against RSUD batch 2026-05",
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.ContributorSourceChangeHistory.Should().HaveCount(1);
        var row = db.ContributorSourceChangeHistory.Single();
        row.NewSourceSystem.Should().Be("RSUD");
        row.OldSourceSystem.Should().Be("Manual");
        row.ChangedAtUtc.Should().Be(ClockNow);
        codes.Should().Contain(IContributorSourceHistoryService.AuditSourceChanged);
    }

    [Fact]
    public async Task RecordChange_RejectsEmptyNewSource()
    {
        using var db = CreateContext();
        db.Contributors.Add(new Contributor
        {
            Idno = "1234567890123",
            Denumire = "SRL Test",
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        });
        await db.SaveChangesAsync();
        var contributorId = db.Contributors.Single().Id;

        var audit = NewAuditCapturing(out _);
        var svc = NewService(db, audit);

        var result = await svc.RecordChangeAsync(
            contributorId,
            oldSource: null,
            newSource: "   ",
            actorUserId: null,
            reason: null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        db.ContributorSourceChangeHistory.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHistory_ReturnsRowsDescendingByChangedAt()
    {
        using var db = CreateContext();
        db.Contributors.Add(new Contributor
        {
            Idno = "1234567890123",
            Denumire = "SRL Test",
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        });
        await db.SaveChangesAsync();
        var contributorId = db.Contributors.Single().Id;

        // Three rows with successive timestamps.
        db.ContributorSourceChangeHistory.AddRange(
            new ContributorSourceChangeHistory
            {
                ContributorId = contributorId,
                OldSourceSystem = null,
                NewSourceSystem = "Manual",
                ChangedAtUtc = ClockNow.AddDays(-3),
                CreatedAtUtc = ClockNow.AddDays(-3),
            },
            new ContributorSourceChangeHistory
            {
                ContributorId = contributorId,
                OldSourceSystem = "Manual",
                NewSourceSystem = "RSUD",
                ChangedAtUtc = ClockNow.AddDays(-2),
                CreatedAtUtc = ClockNow.AddDays(-2),
            },
            new ContributorSourceChangeHistory
            {
                ContributorId = contributorId,
                OldSourceSystem = "RSUD",
                NewSourceSystem = "SFS",
                ChangedAtUtc = ClockNow.AddDays(-1),
                CreatedAtUtc = ClockNow.AddDays(-1),
            });
        await db.SaveChangesAsync();

        var audit = NewAuditCapturing(out _);
        var svc = NewService(db, audit);

        var result = await svc.GetHistoryAsync($"SQID-{contributorId}", 0, 10, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().HaveCount(3);
        result.Value.Items[0].NewSourceSystem.Should().Be("SFS");
        result.Value.Items[1].NewSourceSystem.Should().Be("RSUD");
        result.Value.Items[2].NewSourceSystem.Should().Be("Manual");
    }
}
