using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Unit tests for <see cref="AuditService"/> after R0186 — the service is now a thin
/// enqueue facade; persistence + MLog mirroring happen on the <see cref="AuditDrainer"/>
/// path (covered separately by <see cref="AuditDrainerTests"/>).
/// </summary>
/// <remarks>
/// SEC 044 / CLAUDE.md §5.6 enforcement: every record handed off to the queue must
/// already be redacted — callers can never sneak unredacted PII onto the channel,
/// because the redactor sits at the head of <see cref="AuditService.RecordAsync"/>.
/// Member of <see cref="CnasMeterCollection"/> — the enqueue path emits
/// <c>cnas.audit.enqueued</c> on the static meter (R0040).
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public class AuditServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task RecordAsync_WithPiiInDetails_EnqueuesRedactedRecord()
    {
        // Arrange
        var queue = new AuditWriteQueue();
        var clock = new StubClock(ClockNow);
        var service = new AuditService(queue, clock, NullLogger<AuditService>.Instance);
        const string callerJson = """{"idnp":"2000123456782","email":"user@example.md","status":"ok"}""";

        // Act
        var outcome = await service.RecordAsync(
            eventCode: "TEST.PII.REDACTION",
            severity: AuditSeverity.Critical,
            actorId: "actor-1",
            targetEntity: "Application",
            targetEntityId: 42L,
            detailsJson: callerJson,
            sourceIp: "127.0.0.1",
            correlationId: "corr-1");

        // Assert — outcome success
        outcome.IsSuccess.Should().BeTrue();

        // Assert — exactly one record on the queue, fully redacted.
        queue.Reader.TryRead(out var record).Should().BeTrue();
        record.Should().NotBeNull();
        record!.EventCode.Should().Be("TEST.PII.REDACTION");
        record.Severity.Should().Be(AuditSeverity.Critical);
        record.ActorId.Should().Be("actor-1");
        record.TargetEntity.Should().Be("Application");
        record.TargetEntityId.Should().Be(42L);
        record.SourceIp.Should().Be("127.0.0.1");
        record.CorrelationId.Should().Be("corr-1");
        record.EventAtUtc.Should().Be(ClockNow);
        record.DetailsJson.Should().Contain("\"idnp\":\"[redacted]\"");
        record.DetailsJson.Should().Contain("\"email\":\"[redacted]\"");
        record.DetailsJson.Should().Contain("\"status\":\"ok\"");
        record.DetailsJson.Should().NotContain("2000123456782");
        record.DetailsJson.Should().NotContain("user@example.md");

        // Channel is now empty.
        queue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task RecordAsync_QueueFull_ReturnsInternalErrorAndLogs()
    {
        // Arrange — fill the bounded queue to capacity (4096 records).
        var queue = new AuditWriteQueue();
        var clock = new StubClock(ClockNow);
        var service = new AuditService(queue, clock, NullLogger<AuditService>.Instance);

        for (var i = 0; i < AuditWriteQueue.Capacity; i++)
        {
            var ok = await service.RecordAsync(
                eventCode: "TEST.FILL",
                severity: AuditSeverity.Information,
                actorId: "actor",
                targetEntity: null,
                targetEntityId: null,
                detailsJson: "{}",
                sourceIp: null,
                correlationId: null);
            ok.IsSuccess.Should().BeTrue();
        }

        // Act — one more should overflow.
        var overflow = await service.RecordAsync(
            eventCode: "TEST.OVERFLOW",
            severity: AuditSeverity.Information,
            actorId: "actor",
            targetEntity: null,
            targetEntityId: null,
            detailsJson: "{}",
            sourceIp: null,
            correlationId: null);

        // Assert
        overflow.IsSuccess.Should().BeFalse();
        overflow.ErrorCode.Should().Be(ErrorCodes.Internal);
        overflow.ErrorMessage.Should().Contain("queue", "operator-facing diagnostic should mention the queue");
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }
}
