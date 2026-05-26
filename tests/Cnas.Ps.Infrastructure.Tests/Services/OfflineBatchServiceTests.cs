using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Interop.Batch;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Interop.Batch;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R2161 / TOR INT 002 — RED→GREEN tests for
/// <see cref="OfflineBatchService"/>. Covers ingest happy path, export happy
/// path, status lookup, the 10 000-row payload cap, and the audit emission.
/// </summary>
public sealed class OfflineBatchServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Three-row ingest payload reused by happy-path tests.</summary>
    private static readonly string[] ThreeRowPayload = { "row-1", "row-2", "row-3" };

    /// <summary>Two-filter export payload reused by the export happy-path test.</summary>
    private static readonly string[] TwoFilterPayload = { "region=Chisinau", "status=Active" };

    /// <summary>Single-row payload reused for status + scoping checks.</summary>
    private static readonly string[] SingleRowPayload = { "r1" };

    /// <summary>R2161 — ingest happy path returns a Pending row + emits the ingest audit.</summary>
    [Fact]
    public async Task SubmitIngest_HappyPath_PersistsPendingRowAndAuditsIngestCode()
    {
        using var db = CreateContext();
        var (svc, auditCodes) = NewService(db, callerUserId: 42L, callerSqid: "USER-42");

        var input = new OfflineBatchIngestInputDto(
            Description: "Demographic delta refresh",
            Rows: ThreeRowPayload);

        var result = await svc.SubmitIngestAsync(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.Kind.Should().Be(nameof(OfflineBatchJobKind.Ingest));
        result.Value.Status.Should().Be(nameof(OfflineBatchJobStatus.Pending));
        result.Value.RowCount.Should().Be(3);
        auditCodes.Should().Contain(IOfflineBatchService.AuditIngestSubmitted);

        var persisted = await db.OfflineBatchJobs.SingleAsync();
        persisted.Kind.Should().Be(OfflineBatchJobKind.Ingest);
        persisted.Status.Should().Be(OfflineBatchJobStatus.Pending);
        persisted.SubmittedByUserId.Should().Be(42L);
        persisted.SubmittedAtUtc.Should().Be(ClockNow);
        persisted.RowCount.Should().Be(3);
    }

    /// <summary>R2161 — export happy path returns a Pending row + emits the export audit.</summary>
    [Fact]
    public async Task SubmitExport_HappyPath_PersistsPendingRowAndAuditsExportCode()
    {
        using var db = CreateContext();
        var (svc, auditCodes) = NewService(db, callerUserId: 7L, callerSqid: "USER-7");

        var input = new OfflineBatchExportInputDto(
            Description: "Pensioners by region",
            Filters: TwoFilterPayload);

        var result = await svc.SubmitExportAsync(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.Kind.Should().Be(nameof(OfflineBatchJobKind.Export));
        result.Value.Status.Should().Be(nameof(OfflineBatchJobStatus.Pending));
        result.Value.RowCount.Should().Be(2);
        auditCodes.Should().Contain(IOfflineBatchService.AuditExportSubmitted);
    }

    /// <summary>R2161 — GetStatusAsync returns the row for its owner.</summary>
    [Fact]
    public async Task GetStatus_OwnedRow_ReturnsCurrentSnapshot()
    {
        using var db = CreateContext();
        var (svc, _) = NewService(db, callerUserId: 11L, callerSqid: "USER-11");

        var submit = await svc.SubmitIngestAsync(new OfflineBatchIngestInputDto(
            Description: null,
            Rows: SingleRowPayload));
        submit.IsSuccess.Should().BeTrue();

        var status = await svc.GetStatusAsync(submit.Value.Id);

        status.IsSuccess.Should().BeTrue();
        status.Value.Id.Should().Be(submit.Value.Id);
        status.Value.Status.Should().Be(nameof(OfflineBatchJobStatus.Pending));
    }

    /// <summary>R2161 — oversized payload (10 001 rows) rejected with PAYLOAD_TOO_LARGE.</summary>
    [Fact]
    public async Task SubmitIngest_OversizedPayload_ReturnsPayloadTooLarge()
    {
        using var db = CreateContext();
        var (svc, _) = NewService(db, callerUserId: 1L, callerSqid: "USER-1");

        var oversized = new string[IOfflineBatchService.MaxRows + 1];
        for (var i = 0; i < oversized.Length; i++)
        {
            oversized[i] = $"row-{i}";
        }

        var result = await svc.SubmitIngestAsync(new OfflineBatchIngestInputDto(
            Description: "bad",
            Rows: oversized));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(IOfflineBatchService.PayloadTooLargeCode);

        // No persistence side-effect on rejection.
        (await db.OfflineBatchJobs.CountAsync()).Should().Be(0);
    }

    /// <summary>R2161 — GetStatusAsync scoped to caller — another user's row surfaces as NotFound.</summary>
    [Fact]
    public async Task GetStatus_OtherUsersRow_ReturnsNotFound()
    {
        using var db = CreateContext();

        // Alice submits a job.
        var (alice, _) = NewService(db, callerUserId: 100L, callerSqid: "USER-100");
        var aliceJob = await alice.SubmitIngestAsync(new OfflineBatchIngestInputDto(
            Description: "private",
            Rows: SingleRowPayload));
        aliceJob.IsSuccess.Should().BeTrue();

        // Bob asks for Alice's row.
        var (bob, _) = NewService(db, callerUserId: 200L, callerSqid: "USER-200");
        var result = await bob.GetStatusAsync(aliceJob.Value.Id);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>Builds a fresh EF Core InMemory context for the test.</summary>
    /// <returns>A fresh writer-side DbContext.</returns>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-offline-batch-job-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Constructs a service wired against the supplied caller context.</summary>
    /// <param name="db">EF Core context backing the test.</param>
    /// <param name="callerUserId">Internal user id of the caller.</param>
    /// <param name="callerSqid">Sqid attribution string for the caller.</param>
    /// <returns>A tuple of the service + a list capturing every audit event code raised.</returns>
    private static (OfflineBatchService Service, List<string> AuditCodes) NewService(
        ICnasDbContext db,
        long callerUserId,
        string callerSqid)
    {
        var clock = Substitute.For<ICnasTimeProvider>();
        clock.UtcNow.Returns(ClockNow);

        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(c => $"SQID-{c.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(c =>
        {
            var v = c.Arg<string>();
            if (v is not null && v.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(v["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });

        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(callerUserId);
        caller.UserSqid.Returns(callerSqid);
        caller.SourceIp.Returns("203.0.113.7");
        caller.CorrelationId.Returns("corr-offline-batch");

        var auditCodes = new List<string>();
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                auditCodes.Add(c.ArgAt<string>(0));
                return Task.FromResult(Result.Success());
            });

        return (new OfflineBatchService(db, clock, sqids, caller, audit), auditCodes);
    }
}
