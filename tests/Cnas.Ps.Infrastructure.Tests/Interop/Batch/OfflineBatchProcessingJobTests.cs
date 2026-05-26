using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Interop.Batch;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Interop.Batch;
using Cnas.Ps.Infrastructure.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — tests for <see cref="OfflineBatchProcessingJob"/>.
/// </summary>
public sealed class OfflineBatchProcessingJobTests
{
    private static IJobExecutionContext NewExecCtx()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private static IServiceScopeFactory NewScopeFactory(
        CnasDbContext db,
        IOfflineBatchProcessor processor)
    {
        var sqids = BatchTestHelpers.NewSqidMock();
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ICnasDbContext)).Returns(db);
        sp.GetService(typeof(ISqidService)).Returns(sqids);
        sp.GetService(typeof(IOfflineBatchProcessor)).Returns(processor);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    /// <summary>R1710 — peak-hour gate Skip turns the fire into a no-op.</summary>
    [Fact]
    public async Task Execute_PeakHourGateSkip_NoOps()
    {
        using var db = BatchTestHelpers.CreateContext();
        var processor = Substitute.For<IOfflineBatchProcessor>();
        var job = new OfflineBatchProcessingJob(
            NewScopeFactory(db, processor),
            new AlwaysSkipPeakHourGate(),
            NullLogger<OfflineBatchProcessingJob>.Instance);

        await job.Execute(NewExecCtx());

        await processor.DidNotReceive().ProcessAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>R1710 — happy path picks the oldest Queued submission and finalises it.</summary>
    [Fact]
    public async Task Execute_HappyPath_PicksOldestQueuedAndProcesses()
    {
        using var db = BatchTestHelpers.CreateContext();
        var blobs = new InMemoryOfflineBatchBlobStore();
        var signer = BatchTestHelpers.NewSigner();
        var interop = BatchTestHelpers.NewSuccessInteropApi();

        // Seed two Queued submissions; oldest must be picked first.
        var csv = BatchTestHelpers.BuildGetInsuredPersonStatusCsv("2000123456782");
        var key = await blobs.PutAsync(csv, "text/csv");
        var oldest = new OfflineBatchSubmission
        {
            BatchNumber = "OBS-2026-000001",
            ConsumerSubject = "client",
            OpCode = AnnexFourBatchOp.GetInsuredPersonStatus,
            Status = OfflineBatchStatus.Queued,
            RequestFileName = "a.csv",
            RequestFileSizeBytes = csv.LongLength,
            RequestFileHashSha256 = BatchTestHelpers.Sha256Hex(csv),
            RequestFileStorageKey = key,
            RequestRowCount = 1,
            SubmittedAt = BatchTestHelpers.ClockNow.AddHours(-1),
            CreatedAtUtc = BatchTestHelpers.ClockNow.AddHours(-1),
            IsActive = true,
        };
        var newer = new OfflineBatchSubmission
        {
            BatchNumber = "OBS-2026-000002",
            ConsumerSubject = "client",
            OpCode = AnnexFourBatchOp.GetInsuredPersonStatus,
            Status = OfflineBatchStatus.Queued,
            RequestFileName = "b.csv",
            RequestFileSizeBytes = csv.LongLength,
            RequestFileHashSha256 = BatchTestHelpers.Sha256Hex(csv),
            RequestFileStorageKey = await blobs.PutAsync(csv, "text/csv"),
            RequestRowCount = 1,
            SubmittedAt = BatchTestHelpers.ClockNow,
            CreatedAtUtc = BatchTestHelpers.ClockNow,
            IsActive = true,
        };
        db.OfflineBatchSubmissions.AddRange(oldest, newer);
        await db.SaveChangesAsync();

        // Seed one Pending row per submission so the processor can iterate.
        db.OfflineBatchRows.AddRange(
            new OfflineBatchRow
            {
                SubmissionId = oldest.Id,
                RowOrdinal = 1,
                Status = OfflineBatchRowStatus.Pending,
                RequestPayloadJson = System.Text.Json.JsonSerializer.Serialize(new { Idnp = "2000123456782" }),
                CreatedAtUtc = BatchTestHelpers.ClockNow.AddHours(-1),
                IsActive = true,
            },
            new OfflineBatchRow
            {
                SubmissionId = newer.Id,
                RowOrdinal = 1,
                Status = OfflineBatchRowStatus.Pending,
                RequestPayloadJson = System.Text.Json.JsonSerializer.Serialize(new { Idnp = "2000123456782" }),
                CreatedAtUtc = BatchTestHelpers.ClockNow,
                IsActive = true,
            });
        await db.SaveChangesAsync();

        var audit = BatchTestHelpers.NewAuditCapturing(out _);
        var processor = BatchTestHelpers.NewProcessor(db, audit, blobs, signer, interop);
        var job = new OfflineBatchProcessingJob(
            NewScopeFactory(db, processor),
            new AllowAllPeakHourGate(),
            NullLogger<OfflineBatchProcessingJob>.Instance);

        await job.Execute(NewExecCtx());

        var refreshedOldest = await db.OfflineBatchSubmissions.SingleAsync(s => s.Id == oldest.Id);
        refreshedOldest.Status.Should().Be(OfflineBatchStatus.Completed);
        var refreshedNewer = await db.OfflineBatchSubmissions.SingleAsync(s => s.Id == newer.Id);
        refreshedNewer.Status.Should().Be(OfflineBatchStatus.Queued);
    }
}
