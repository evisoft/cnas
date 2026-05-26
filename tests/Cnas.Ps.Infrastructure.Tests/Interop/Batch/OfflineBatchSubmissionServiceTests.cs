using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Interop.Batch;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Tests.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — tests for
/// <see cref="OfflineBatchSubmissionService"/>.
/// </summary>
public sealed class OfflineBatchSubmissionServiceTests
{
    /// <summary>R1710 — submit happy path emits BATCH.SUBMITTED, parses rows, transitions to Queued.</summary>
    [Fact]
    public async Task Submit_HappyPath_TransitionsToQueuedAndAudits()
    {
        using var db = BatchTestHelpers.CreateContext();
        var audit = BatchTestHelpers.NewAuditCapturing(out var auditCodes);
        var blobs = new InMemoryOfflineBatchBlobStore();
        var parser = new OfflineBatchRequestParser(new OfflineBatchOpSchemaRegistry());
        var svc = BatchTestHelpers.NewService(db, audit, blobs, parser);

        var csv = BatchTestHelpers.BuildGetInsuredPersonStatusCsv("2000123456782", "1234567890123");
        var hash = BatchTestHelpers.Sha256Hex(csv);
        var input = new OfflineBatchSubmissionInputDto(
            ConsumerSubject: "client-rsp",
            OpCode: nameof(AnnexFourBatchOp.GetInsuredPersonStatus),
            RequestFileName: "req.csv",
            RequestFileBytes: csv,
            RequestFileHashSha256: hash);

        var result = await svc.SubmitAsync(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(OfflineBatchStatus.Queued));
        result.Value.RequestRowCount.Should().Be(2);
        auditCodes.Should().Contain(OfflineBatchSubmissionService.AuditBatchSubmitted);

        var persisted = await db.OfflineBatchSubmissions.SingleAsync();
        persisted.Status.Should().Be(OfflineBatchStatus.Queued);
        persisted.ConsumerSubject.Should().Be("client-rsp");
        (await db.OfflineBatchRows.CountAsync()).Should().Be(2);
    }

    /// <summary>R1710 — cancel rejects when the submission is Running.</summary>
    [Fact]
    public async Task Cancel_OnRunning_ReturnsConflict()
    {
        using var db = BatchTestHelpers.CreateContext();
        var audit = BatchTestHelpers.NewAuditCapturing(out _);
        var blobs = new InMemoryOfflineBatchBlobStore();
        var parser = new OfflineBatchRequestParser(new OfflineBatchOpSchemaRegistry());
        var svc = BatchTestHelpers.NewService(db, audit, blobs, parser);

        // Seed a Running submission directly.
        var sub = new OfflineBatchSubmission
        {
            BatchNumber = "OBS-2026-000001",
            ConsumerSubject = "client",
            OpCode = AnnexFourBatchOp.GetInsuredPersonStatus,
            Status = OfflineBatchStatus.Running,
            RequestFileName = "req.csv",
            RequestFileSizeBytes = 10,
            RequestFileHashSha256 = new string('a', 64),
            RequestFileStorageKey = "k",
            RequestRowCount = 1,
            SubmittedAt = BatchTestHelpers.ClockNow,
            CreatedAtUtc = BatchTestHelpers.ClockNow,
            IsActive = true,
        };
        db.OfflineBatchSubmissions.Add(sub);
        await db.SaveChangesAsync();

        var result = await svc.CancelAsync($"SQID-{sub.Id}", new OfflineBatchReasonInputDto("operator requested cancel"));
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Cnas.Ps.Core.Common.ErrorCodes.Conflict);
    }

    /// <summary>R1710 — list filters by consumer subject.</summary>
    [Fact]
    public async Task List_FiltersByConsumerSubject()
    {
        using var db = BatchTestHelpers.CreateContext();
        var audit = BatchTestHelpers.NewAuditCapturing(out _);
        var blobs = new InMemoryOfflineBatchBlobStore();
        var parser = new OfflineBatchRequestParser(new OfflineBatchOpSchemaRegistry());
        var svc = BatchTestHelpers.NewService(db, audit, blobs, parser);

        db.OfflineBatchSubmissions.AddRange(
            new OfflineBatchSubmission
            {
                BatchNumber = "OBS-2026-000001",
                ConsumerSubject = "a",
                OpCode = AnnexFourBatchOp.GetInsuredPersonStatus,
                Status = OfflineBatchStatus.Queued,
                RequestFileName = "a.csv",
                RequestFileSizeBytes = 1,
                RequestFileHashSha256 = new string('a', 64),
                RequestFileStorageKey = "k1",
                SubmittedAt = BatchTestHelpers.ClockNow,
                CreatedAtUtc = BatchTestHelpers.ClockNow,
                IsActive = true,
            },
            new OfflineBatchSubmission
            {
                BatchNumber = "OBS-2026-000002",
                ConsumerSubject = "b",
                OpCode = AnnexFourBatchOp.GetInsuredPersonStatus,
                Status = OfflineBatchStatus.Queued,
                RequestFileName = "b.csv",
                RequestFileSizeBytes = 1,
                RequestFileHashSha256 = new string('a', 64),
                RequestFileStorageKey = "k2",
                SubmittedAt = BatchTestHelpers.ClockNow,
                CreatedAtUtc = BatchTestHelpers.ClockNow,
                IsActive = true,
            });
        await db.SaveChangesAsync();

        var aFilter = new OfflineBatchSubmissionFilterDto(ConsumerSubject: "a");
        var aResult = await svc.ListAsync(aFilter);
        aResult.IsSuccess.Should().BeTrue();
        aResult.Value.Total.Should().Be(1);
        aResult.Value.Items[0].ConsumerSubject.Should().Be("a");
    }
}
