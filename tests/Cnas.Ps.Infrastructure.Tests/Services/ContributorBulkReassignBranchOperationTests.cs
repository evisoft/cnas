using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.BulkActions;
using Cnas.Ps.Application.BulkActions.Operations;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0305 / BP 1.8 / TOR Annex 1 — tests for
/// <see cref="ContributorBulkReassignBranchOperation"/>. Verifies the happy path
/// (two rows reassigned + Notice audit per row), the unknown-branch failure mode,
/// and the no-op already-at-branch path. Uses EF Core InMemory and NSubstitute
/// for the surrounding collaborators (clock, caller, audit).
/// </summary>
public sealed class ContributorBulkReassignBranchOperationTests
{
    /// <summary>Deterministic UTC clock used for audit/snapshot stability.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>First valid IDNO (mod-10 OK; matches the wider Contributor service suite).</summary>
    private const string ValidIdnoA = "1003600012346";

    /// <summary>Second valid IDNO; used to seed a second contributor row.</summary>
    private const string ValidIdnoB = "2000000000006";

    /// <summary>Builds a fresh InMemory <see cref="CnasDbContext"/> with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-contrib-bulk-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Builds a deterministic clock anchored at <see cref="ClockNow"/>.</summary>
    private static ICnasTimeProvider BuildClock()
    {
        var clock = Substitute.For<ICnasTimeProvider>();
        clock.UtcNow.Returns(ClockNow);
        return clock;
    }

    /// <summary>
    /// Builds an audit-service stub that captures every <c>eventCode</c> into
    /// <paramref name="sink"/> so the test can assert per-row emission counts.
    /// </summary>
    /// <param name="sink">Collector for event codes; populated as a side effect.</param>
    /// <returns>Substitute that records <see cref="Result.Success"/> for every call.</returns>
    private static IAuditService BuildAuditCapturing(List<string> sink)
    {
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Do<string>(c => sink.Add(c)),
                Arg.Any<AuditSeverity>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<long?>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        return audit;
    }

    /// <summary>Builds a stub <see cref="ICallerContext"/> for attribution.</summary>
    private static ICallerContext BuildCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns("SQID-CALLER");
        caller.SourceIp.Returns("127.0.0.1");
        caller.CorrelationId.Returns("corr-bulk");
        return caller;
    }

    /// <summary>Seeds an active <see cref="CnasBranch"/> row with the supplied code.</summary>
    /// <param name="db">Test context.</param>
    /// <param name="code">Natural code (e.g. <c>"CNAS-CHIS-CTR"</c>).</param>
    private static async Task SeedBranchAsync(CnasDbContext db, string code)
    {
        db.CnasBranches.Add(new CnasBranch
        {
            Code = code,
            Name = code,
            City = "Chișinău",
            CreatedAtUtc = ClockNow.AddDays(-10),
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Seeds an active <see cref="Contributor"/> row with the supplied IDNO + branch.</summary>
    private static async Task<Contributor> SeedContributorAsync(
        CnasDbContext db,
        string idno,
        string? currentBranchCode)
    {
        var entity = new Contributor
        {
            Idno = idno,
            IdnoHash = IdHashHelper.Hash(idno),
            Denumire = $"SRL {idno}",
            RegisteredAtUtc = ClockNow.AddDays(-30),
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
            CnasBranchCode = currentBranchCode,
        };
        db.Contributors.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    /// <summary>
    /// Happy path — two contributors get re-pointed at a new branch, two Notice
    /// CONTRIBUTOR.BRANCH_REASSIGNED audit rows are emitted.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_HappyPath_ReassignsTwoRowsAndAuditsEach()
    {
        await using var db = CreateContext();
        await SeedBranchAsync(db, "CNAS-BALTI");
        await SeedBranchAsync(db, "CNAS-CHIS-CTR");
        var c1 = await SeedContributorAsync(db, ValidIdnoA, currentBranchCode: "CNAS-BALTI");
        var c2 = await SeedContributorAsync(db, ValidIdnoB, currentBranchCode: "CNAS-BALTI");

        var auditEvents = new List<string>();
        var op = new ContributorBulkReassignBranchOperation(db, BuildClock(), BuildAuditCapturing(auditEvents));
        var parameters = JsonSerializer.Serialize(new { newBranchCode = "CNAS-CHIS-CTR" });
        var caller = BuildCaller();

        var outcome1 = await op.ExecuteAsync(c1.Id, parameters, caller, CancellationToken.None);
        var outcome2 = await op.ExecuteAsync(c2.Id, parameters, caller, CancellationToken.None);

        outcome1.Success.Should().BeTrue();
        outcome2.Success.Should().BeTrue();

        var reloaded = await db.Contributors.OrderBy(c => c.Id).ToListAsync();
        reloaded.Should().OnlyContain(c => c.CnasBranchCode == "CNAS-CHIS-CTR");
        auditEvents.Should().HaveCount(2).And.AllSatisfy(c => c.Should().Be("CONTRIBUTOR.BRANCH_REASSIGNED"));
    }

    /// <summary>Unknown branch code → BRANCH_NOT_FOUND, no mutation.</summary>
    [Fact]
    public async Task ExecuteAsync_UnknownBranch_ReturnsBranchNotFound()
    {
        await using var db = CreateContext();
        await SeedBranchAsync(db, "CNAS-BALTI");
        var c1 = await SeedContributorAsync(db, ValidIdnoA, currentBranchCode: "CNAS-BALTI");

        var auditEvents = new List<string>();
        var op = new ContributorBulkReassignBranchOperation(db, BuildClock(), BuildAuditCapturing(auditEvents));
        var parameters = JsonSerializer.Serialize(new { newBranchCode = "CNAS-NOPE" });

        var outcome = await op.ExecuteAsync(c1.Id, parameters, BuildCaller(), CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.ErrorCode.Should().Be("BRANCH_NOT_FOUND");
        var reloaded = await db.Contributors.SingleAsync(c => c.Id == c1.Id);
        reloaded.CnasBranchCode.Should().Be("CNAS-BALTI");
        auditEvents.Should().BeEmpty();
    }

    /// <summary>Already-at-branch → ALREADY_AT_BRANCH no-op surfaced as a failure outcome.</summary>
    [Fact]
    public async Task ExecuteAsync_AlreadyAtBranch_ReturnsAlreadyAtBranch()
    {
        await using var db = CreateContext();
        await SeedBranchAsync(db, "CNAS-CHIS-CTR");
        var c1 = await SeedContributorAsync(db, ValidIdnoA, currentBranchCode: "CNAS-CHIS-CTR");

        var auditEvents = new List<string>();
        var op = new ContributorBulkReassignBranchOperation(db, BuildClock(), BuildAuditCapturing(auditEvents));
        var parameters = JsonSerializer.Serialize(new { newBranchCode = "CNAS-CHIS-CTR" });

        var outcome = await op.ExecuteAsync(c1.Id, parameters, BuildCaller(), CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.ErrorCode.Should().Be("ALREADY_AT_BRANCH");
        auditEvents.Should().BeEmpty();
    }

    /// <summary>Descriptor surface — operation declares the canonical contract.</summary>
    [Fact]
    public void Descriptor_DeclaresCanonicalContract()
    {
        using var db = CreateContext();
        var op = new ContributorBulkReassignBranchOperation(db, BuildClock(), BuildAuditCapturing(new List<string>()));

        op.Code.Should().Be("Contributor.ReassignBranch");
        op.Registry.Should().Be(BulkRegistries.Contributor);
        op.RequiredPermission.Should().Be("Contributor.Manage");
        op.MaxRowsPerRun.Should().Be(1_000);
        op.RequiresParameters.Should().BeTrue();
    }
}
