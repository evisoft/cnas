using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Attachments;
using Cnas.Ps.Application.Reports;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Reports.Adhoc;

/// <summary>
/// R0583 / TOR CF 09.06 / CF 09.09 — drain-half tests for
/// <see cref="ReportJobRunner"/>. All collaborators are NSubstitute fakes so
/// the tests stay focused on the runner's state-machine behaviour.
/// </summary>
public class ReportJobRunnerTests
{
    /// <summary>Deterministic clock anchor.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 11, 0, 0, DateTimeKind.Utc);

    /// <summary>Stub CSV payload returned by the engine on the success path.</summary>
    private static readonly byte[] StubBytes = [0x68, 0x65, 0x6C, 0x6C, 0x6F]; // "hello"

    [Fact]
    public async Task RunNextAsync_NoQueuedJobs_ReturnsSuccessWithNullValue()
    {
        var h = Harness.Create();

        var result = await h.Runner.RunNextAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task RunNextAsync_PicksOldestQueuedRow_FlipsToSucceeded_PopulatesAttachment()
    {
        var h = Harness.Create();
        var older = await h.SeedJobAsync(QueuedAt: ClockNow.AddMinutes(-10));
        var newer = await h.SeedJobAsync(QueuedAt: ClockNow.AddMinutes(-1));

        h.Engine.ExportAsync(Arg.Any<long>(), Arg.Any<ExportFormat>(), Arg.Any<CancellationToken>())
            .Returns(Result<byte[]>.Success(StubBytes));

        var result = await h.Runner.RunNextAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Status.Should().Be(ReportJobStatus.Succeeded.ToString());
        result.Value.AttachmentSqid.Should().NotBeNullOrEmpty();

        var olderRow = await h.Db.ReportJobs.SingleAsync(j => j.Id == older.Id);
        olderRow.Status.Should().Be(ReportJobStatus.Succeeded);
        olderRow.AttachmentRecordId.Should().NotBeNull();
        var newerRow = await h.Db.ReportJobs.SingleAsync(j => j.Id == newer.Id);
        newerRow.Status.Should().Be(ReportJobStatus.Queued);
    }

    [Fact]
    public async Task RunNextAsync_EngineFails_FlipsToFailed_AndStampsFailureReason()
    {
        var h = Harness.Create();
        await h.SeedJobAsync();
        h.Engine.ExportAsync(Arg.Any<long>(), Arg.Any<ExportFormat>(), Arg.Any<CancellationToken>())
            .Returns(Result<byte[]>.Failure(ErrorCodes.QueryTooBroad, "Too broad."));

        var result = await h.Runner.RunNextAsync();

        result.IsSuccess.Should().BeTrue();
        var row = await h.Db.ReportJobs.SingleAsync();
        row.Status.Should().Be(ReportJobStatus.Failed);
        row.FailureReason.Should().Contain("Too broad");

        await h.Audit.Received().RecordAsync(
            "REPORT.JOB.FAILED",
            AuditSeverity.Information,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunNextAsync_OnSuccess_DispatchesReportReadyNotification()
    {
        var h = Harness.Create();
        var job = await h.SeedJobAsync();
        h.Engine.ExportAsync(Arg.Any<long>(), Arg.Any<ExportFormat>(), Arg.Any<CancellationToken>())
            .Returns(Result<byte[]>.Success(StubBytes));

        await h.Runner.RunNextAsync();

        await h.Notifications.Received().EnqueueAsync(
            job.RequestedByUserId,
            ReportJobRunner.SubjectReady,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunNextAsync_OnFailure_DispatchesReportFailedNotification()
    {
        var h = Harness.Create();
        var job = await h.SeedJobAsync();
        h.Engine.ExportAsync(Arg.Any<long>(), Arg.Any<ExportFormat>(), Arg.Any<CancellationToken>())
            .Returns(Result<byte[]>.Failure(ErrorCodes.ValidationFailed, "Boom."));

        await h.Runner.RunNextAsync();

        await h.Notifications.Received().EnqueueAsync(
            job.RequestedByUserId,
            ReportJobRunner.SubjectFailed,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunBatchAsync_DrainsUpToMaxJobs_Sequentially()
    {
        var h = Harness.Create();
        for (var i = 0; i < 5; i++)
        {
            await h.SeedJobAsync(QueuedAt: ClockNow.AddMinutes(-(10 - i)));
        }
        h.Engine.ExportAsync(Arg.Any<long>(), Arg.Any<ExportFormat>(), Arg.Any<CancellationToken>())
            .Returns(Result<byte[]>.Success(StubBytes));

        var drained = await h.Runner.RunBatchAsync(maxJobs: 10);

        drained.Should().Be(5);
        (await h.Db.ReportJobs.CountAsync(j => j.Status == ReportJobStatus.Succeeded)).Should().Be(5);
    }

    // ─────────────────────── Harness ───────────────────────

    /// <summary>Deterministic clock implementation.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Tracks attachments handed to the fake service (id assigned sequentially).</summary>
    private sealed class Harness
    {
        public const long RequesterId = 7700L;

        public required CnasDbContext Db { get; init; }
        public required ReportJobRunner Runner { get; init; }
        public required IReportEngine Engine { get; init; }
        public required IAttachmentService Attachments { get; init; }
        public required INotificationService Notifications { get; init; }
        public required IAuditService Audit { get; init; }

        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-reportjobrunner-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);

            var clock = new StubClock(ClockNow);

            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
            sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
            {
                var arg = call.Arg<string?>();
                if (!string.IsNullOrEmpty(arg)
                    && arg.StartsWith("SQID-", StringComparison.Ordinal)
                    && long.TryParse(arg.AsSpan(5), out var n))
                {
                    return Result<long>.Success(n);
                }
                return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
            });

            var engine = Substitute.For<IReportEngine>();

            // Attachment service fake: return a deterministic Sqid whose decode round-trips.
            var attachments = Substitute.For<IAttachmentService>();
            var nextAttachmentId = 1000L;
            attachments.UploadAsync(Arg.Any<AttachmentUploadDto>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var id = nextAttachmentId++;
                    var dto = new AttachmentRecordDto(
                        Id: $"SQID-{id}",
                        OwnerEntityType: "ReportJob",
                        OwnerSqid: call.Arg<AttachmentUploadDto>().OwnerSqid,
                        FileName: call.Arg<AttachmentUploadDto>().DeclaredFileName,
                        ContentType: "text/csv",
                        SizeBytes: 5L,
                        Sha256Hex: "deadbeef",
                        Category: "Other",
                        SensitivityLabel: "Confidential",
                        Description: null,
                        UploadedByUserSqid: $"SQID-{RequesterId}",
                        UploadedUtc: ClockNow,
                        IsArchived: false);
                    return Task.FromResult(Result<AttachmentRecordDto>.Success(dto));
                });

            var notifications = Substitute.For<INotificationService>();
            notifications.EnqueueAsync(
                    Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var runner = new ReportJobRunner(
                db, clock, sqids, engine, attachments, notifications, audit);

            return new Harness
            {
                Db = db,
                Runner = runner,
                Engine = engine,
                Attachments = attachments,
                Notifications = notifications,
                Audit = audit,
            };
        }

        public async Task<ReportJob> SeedJobAsync(DateTime? QueuedAt = null)
        {
            var row = new ReportJob
            {
                ReportTemplateId = 42L,
                RequestedByUserId = RequesterId,
                Format = (int)ExportFormat.Csv,
                Status = ReportJobStatus.Queued,
                QueuedAtUtc = QueuedAt ?? ClockNow.AddMinutes(-1),
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.ReportJobs.Add(row);
            await Db.SaveChangesAsync();
            return row;
        }
    }
}
