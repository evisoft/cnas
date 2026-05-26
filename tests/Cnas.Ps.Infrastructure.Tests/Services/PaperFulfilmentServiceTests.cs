using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0602 / TOR CF 11.03 — unit tests for <see cref="PaperFulfilmentService"/>.
/// Exercises the Pending → Printed → Dispatched → Delivered state machine
/// plus the idempotency / illegal-transition guards and the audit-row
/// emission contract.
/// </summary>
public sealed class PaperFulfilmentServiceTests
{
    /// <summary>Deterministic UTC clock instant.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Enqueue happy path: persists a Pending row + emits audit.</summary>
    [Fact]
    public async Task EnqueueAsync_HappyPath_PersistsPendingRow()
    {
        var h = await Harness.CreateAsync();
        var docSqid = $"SQID-{h.DocumentId}";

        var result = await h.Service.EnqueueAsync(docSqid, "CHIS");

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Pending");
        h.Db.PaperFulfilmentRecords.Count().Should().Be(1);
        await h.Audit.Received().RecordAsync(
            "PAPER.ENQUEUED", AuditSeverity.Notice, Arg.Any<string>(),
            nameof(PaperFulfilmentRecord), Arg.Any<long?>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Second enqueue on the same document is rejected.</summary>
    [Fact]
    public async Task EnqueueAsync_DoubleEnqueue_Rejected()
    {
        var h = await Harness.CreateAsync();
        var docSqid = $"SQID-{h.DocumentId}";
        await h.Service.EnqueueAsync(docSqid, "CHIS");

        var second = await h.Service.EnqueueAsync(docSqid, "CHIS");

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.PaperFulfilmentAlreadyEnqueued);
    }

    /// <summary>Pending → Printed transition succeeds.</summary>
    [Fact]
    public async Task MarkPrintedAsync_FromPending_Succeeds()
    {
        var h = await Harness.CreateAsync();
        var enqueue = await h.Service.EnqueueAsync($"SQID-{h.DocumentId}", "CHIS");

        var result = await h.Service.MarkPrintedAsync(enqueue.Value.Id);

        result.IsSuccess.Should().BeTrue();
        var row = h.Db.PaperFulfilmentRecords.Single();
        row.Status.Should().Be(PaperFulfilmentStatus.Printed);
        row.PrintedAtUtc.Should().Be(ClockNow);
    }

    /// <summary>Printed → Dispatched transition captures the tracking number.</summary>
    [Fact]
    public async Task MarkDispatchedAsync_FromPrinted_CapturesTracking()
    {
        var h = await Harness.CreateAsync();
        var enqueue = await h.Service.EnqueueAsync($"SQID-{h.DocumentId}", "CHIS");
        await h.Service.MarkPrintedAsync(enqueue.Value.Id);

        var result = await h.Service.MarkDispatchedAsync(enqueue.Value.Id, "TRACK-12345");

        result.IsSuccess.Should().BeTrue();
        var row = h.Db.PaperFulfilmentRecords.Single();
        row.Status.Should().Be(PaperFulfilmentStatus.Dispatched);
        row.CarrierTrackingNumber.Should().Be("TRACK-12345");
    }

    /// <summary>Dispatched → Delivered transition succeeds.</summary>
    [Fact]
    public async Task MarkDeliveredAsync_FromDispatched_Succeeds()
    {
        var h = await Harness.CreateAsync();
        var enqueue = await h.Service.EnqueueAsync($"SQID-{h.DocumentId}", "CHIS");
        await h.Service.MarkPrintedAsync(enqueue.Value.Id);
        await h.Service.MarkDispatchedAsync(enqueue.Value.Id, "TRACK-12345");

        var date = new DateOnly(2026, 5, 25);
        var result = await h.Service.MarkDeliveredAsync(enqueue.Value.Id, date);

        result.IsSuccess.Should().BeTrue();
        var row = h.Db.PaperFulfilmentRecords.Single();
        row.Status.Should().Be(PaperFulfilmentStatus.Delivered);
        row.DeliveredOn.Should().Be(date);
    }

    /// <summary>Illegal transition (Pending → Delivered) is rejected.</summary>
    [Fact]
    public async Task MarkDeliveredAsync_FromPending_Rejected()
    {
        var h = await Harness.CreateAsync();
        var enqueue = await h.Service.EnqueueAsync($"SQID-{h.DocumentId}", "CHIS");

        var result = await h.Service.MarkDeliveredAsync(enqueue.Value.Id, new DateOnly(2026, 5, 25));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.PaperFulfilmentInvalidTransition);
    }

    /// <summary>
    /// Each state transition emits its dedicated stable audit code so
    /// dashboards can chart fulfilment latency between stages.
    /// </summary>
    [Fact]
    public async Task AllTransitions_EmitDedicatedAuditCodes()
    {
        var h = await Harness.CreateAsync();
        var enqueue = await h.Service.EnqueueAsync($"SQID-{h.DocumentId}", "CHIS");
        await h.Service.MarkPrintedAsync(enqueue.Value.Id);
        await h.Service.MarkDispatchedAsync(enqueue.Value.Id, "TRACK-XYZ");
        await h.Service.MarkDeliveredAsync(enqueue.Value.Id, new DateOnly(2026, 5, 25));

        await h.Audit.Received().RecordAsync(
            "PAPER.ENQUEUED", Arg.Any<AuditSeverity>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await h.Audit.Received().RecordAsync(
            "PAPER.PRINTED", Arg.Any<AuditSeverity>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await h.Audit.Received().RecordAsync(
            "PAPER.DISPATCHED", Arg.Any<AuditSeverity>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await h.Audit.Received().RecordAsync(
            "PAPER.DELIVERED", Arg.Any<AuditSeverity>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ─────────────────────── Harness ───────────────────────

    /// <summary>Deterministic clock.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Bundles the SUT + DB.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required PaperFulfilmentService Service { get; init; }
        public required IAuditService Audit { get; init; }
        public long DocumentId { get; init; }

        public static async Task<Harness> CreateAsync()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-paperfulfil-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);

            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
            sqids.TryDecode(Arg.Any<string>()).Returns(call =>
            {
                var s = call.Arg<string>();
                if (s is null || !s.StartsWith("SQID-", StringComparison.Ordinal))
                {
                    return Result<long>.Failure(ErrorCodes.InvalidSqid, "Not a SQID-prefixed test sqid.");
                }
                return long.TryParse(s["SQID-".Length..], out var id)
                    ? Result<long>.Success(id)
                    : Result<long>.Failure(ErrorCodes.InvalidSqid, "Not a numeric sqid suffix.");
            });

            var clock = new StubClock(ClockNow);
            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(99L);
            caller.UserSqid.Returns("SQID-99");
            caller.Roles.Returns(new[] { RoleCodes.User });

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            // Seed one Document so the foreign key resolves.
            var doc = new Document
            {
                Title = "Decizia pensie",
                MimeType = "application/pdf",
                StorageObjectKey = "k",
                StorageBucket = "b",
                ContentSha256Hex = "abcd",
                SizeBytes = 1,
                Kind = DocumentKind.Decision,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            db.Documents.Add(doc);
            await db.SaveChangesAsync();

            var svc = new PaperFulfilmentService(db, sqids, clock, caller, audit);
            return new Harness { Db = db, Service = svc, Audit = audit, DocumentId = doc.Id };
        }
    }
}
