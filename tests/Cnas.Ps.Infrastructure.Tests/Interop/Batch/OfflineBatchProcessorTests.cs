using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Interop.Batch;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Tests.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — tests for <see cref="OfflineBatchProcessor"/>.
/// </summary>
public sealed class OfflineBatchProcessorTests
{
    private static readonly string[] OneIdnp = { "2000123456782" };
    private static readonly string[] TwoIdnps = { "2000123456782", "1234567890123" };

    private static async Task<OfflineBatchSubmission> SeedQueuedAsync(
        Cnas.Ps.Infrastructure.Persistence.CnasDbContext db,
        Cnas.Ps.Application.Interop.Batch.IOfflineBatchBlobStore blobs,
        string[] idnps)
    {
        var csv = BatchTestHelpers.BuildGetInsuredPersonStatusCsv(idnps);
        var key = await blobs.PutAsync(csv, "text/csv");
        var sub = new OfflineBatchSubmission
        {
            BatchNumber = "OBS-2026-000001",
            ConsumerSubject = "client",
            OpCode = AnnexFourBatchOp.GetInsuredPersonStatus,
            Status = OfflineBatchStatus.Queued,
            RequestFileName = "req.csv",
            RequestFileSizeBytes = csv.LongLength,
            RequestFileHashSha256 = BatchTestHelpers.Sha256Hex(csv),
            RequestFileStorageKey = key,
            RequestRowCount = idnps.Length,
            SubmittedAt = BatchTestHelpers.ClockNow,
            CreatedAtUtc = BatchTestHelpers.ClockNow,
            IsActive = true,
        };
        db.OfflineBatchSubmissions.Add(sub);
        await db.SaveChangesAsync();

        for (int i = 0; i < idnps.Length; i++)
        {
            db.OfflineBatchRows.Add(new OfflineBatchRow
            {
                SubmissionId = sub.Id,
                RowOrdinal = i + 1,
                Status = OfflineBatchRowStatus.Pending,
                RequestPayloadJson = System.Text.Json.JsonSerializer.Serialize(new { Idnp = idnps[i] }),
                CreatedAtUtc = BatchTestHelpers.ClockNow,
                IsActive = true,
            });
        }
        await db.SaveChangesAsync();
        return sub;
    }

    /// <summary>R1710 — happy path: every row succeeds, submission completes.</summary>
    [Fact]
    public async Task Process_HappyPath_Completes()
    {
        using var db = BatchTestHelpers.CreateContext();
        var audit = BatchTestHelpers.NewAuditCapturing(out var codes);
        var blobs = new InMemoryOfflineBatchBlobStore();
        var signer = BatchTestHelpers.NewSigner();
        var interop = BatchTestHelpers.NewSuccessInteropApi();
        var sub = await SeedQueuedAsync(db, blobs, TwoIdnps);
        var processor = BatchTestHelpers.NewProcessor(db, audit, blobs, signer, interop);

        var result = await processor.ProcessAsync($"SQID-{sub.Id}");

        result.IsSuccess.Should().BeTrue();
        var persisted = await db.OfflineBatchSubmissions.SingleAsync();
        persisted.Status.Should().Be(OfflineBatchStatus.Completed);
        persisted.TotalRowsProcessed.Should().Be(2);
        persisted.TotalRowsFailed.Should().Be(0);
        persisted.ResponseFileSignatureBase64.Should().NotBeNullOrEmpty();
        persisted.ResponseFileHashSha256.Should().NotBeNullOrEmpty();
        codes.Should().Contain(OfflineBatchProcessor.AuditBatchCompleted);
    }

    /// <summary>R1710 — per-row NotFound becomes a Failed row but completes overall.</summary>
    [Fact]
    public async Task Process_NotFoundPerRow_FailsRowButCompletesSubmission()
    {
        using var db = BatchTestHelpers.CreateContext();
        var audit = BatchTestHelpers.NewAuditCapturing(out _);
        var blobs = new InMemoryOfflineBatchBlobStore();
        var signer = BatchTestHelpers.NewSigner();
        var interop = BatchTestHelpers.NewNotFoundInteropApi();
        var sub = await SeedQueuedAsync(db, blobs, OneIdnp);
        var processor = BatchTestHelpers.NewProcessor(db, audit, blobs, signer, interop);

        await processor.ProcessAsync($"SQID-{sub.Id}");

        var persisted = await db.OfflineBatchSubmissions.SingleAsync();
        persisted.Status.Should().Be(OfflineBatchStatus.Completed);
        persisted.TotalRowsFailed.Should().Be(1);
        var row = await db.OfflineBatchRows.SingleAsync();
        row.Status.Should().Be(OfflineBatchRowStatus.Failed);
        row.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    /// <summary>R1710 — non-Queued submission surfaces a Conflict.</summary>
    [Fact]
    public async Task Process_NonQueued_ReturnsConflict()
    {
        using var db = BatchTestHelpers.CreateContext();
        var audit = BatchTestHelpers.NewAuditCapturing(out _);
        var blobs = new InMemoryOfflineBatchBlobStore();
        var signer = BatchTestHelpers.NewSigner();
        var interop = BatchTestHelpers.NewSuccessInteropApi();

        var sub = new OfflineBatchSubmission
        {
            BatchNumber = "OBS-2026-X",
            ConsumerSubject = "client",
            OpCode = AnnexFourBatchOp.GetInsuredPersonStatus,
            Status = OfflineBatchStatus.Completed,
            RequestFileName = "x.csv",
            RequestFileSizeBytes = 1,
            RequestFileHashSha256 = new string('a', 64),
            RequestFileStorageKey = "k",
            SubmittedAt = BatchTestHelpers.ClockNow,
            CreatedAtUtc = BatchTestHelpers.ClockNow,
            IsActive = true,
        };
        db.OfflineBatchSubmissions.Add(sub);
        await db.SaveChangesAsync();

        var processor = BatchTestHelpers.NewProcessor(db, audit, blobs, signer, interop);
        var result = await processor.ProcessAsync($"SQID-{sub.Id}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    /// <summary>R1710 — signed CSV verifies via the same signer.</summary>
    [Fact]
    public async Task Process_ProducedCsv_VerifiesViaSigner()
    {
        using var db = BatchTestHelpers.CreateContext();
        var audit = BatchTestHelpers.NewAuditCapturing(out _);
        var blobs = new InMemoryOfflineBatchBlobStore();
        var signer = BatchTestHelpers.NewSigner();
        var interop = BatchTestHelpers.NewSuccessInteropApi();
        var sub = await SeedQueuedAsync(db, blobs, OneIdnp);
        var processor = BatchTestHelpers.NewProcessor(db, audit, blobs, signer, interop);

        await processor.ProcessAsync($"SQID-{sub.Id}");

        var persisted = await db.OfflineBatchSubmissions.SingleAsync();
        persisted.ResponseFileStorageKey.Should().NotBeNull();
        var bytes = await blobs.GetAsync(persisted.ResponseFileStorageKey!);
        var ok = await signer.VerifyAsync(bytes, persisted.ResponseFileSignatureBase64!);
        ok.Should().BeTrue();
    }
}
