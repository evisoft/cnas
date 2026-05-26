using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Audit-log facade. After R0186 this is a thin enqueue surface — the durable write
/// (DB + MLog mirror) happens asynchronously on <see cref="AuditDrainer"/>. PII is
/// stripped at this boundary so callers cannot inadvertently bypass SEC 044 / CLAUDE.md
/// §5.6 by passing freeform JSON onto the channel.
/// </summary>
/// <remarks>
/// <para>
/// The previous synchronous implementation did a per-request <c>SaveChangesAsync</c>
/// (plus a parallel MLog HTTP round-trip on Critical events) inside the request thread.
/// Under load that pinned audit latency to the DB + MLog tail. R0186 moves the actual
/// write off the hot path: producers enqueue an <see cref="AuditEventRecord"/> via the
/// bounded singleton <see cref="AuditWriteQueue"/> and return immediately.
/// </para>
/// <para>
/// Overflow policy: when the queue is full <see cref="RecordAsync"/> logs at
/// <c>LogError</c> and returns <see cref="ErrorCodes.Internal"/>. Audit is best-effort
/// under extreme load — better to drop one record loudly than to wedge the request
/// thread on a backed-up audit pipeline.
/// </para>
/// </remarks>
public sealed class AuditService : IAuditService
{
    private readonly AuditWriteQueue _queue;
    private readonly ICnasTimeProvider _clock;
    private readonly ILogger<AuditService> _logger;

    /// <summary>Constructs the audit facade with its enqueue dependencies.</summary>
    /// <param name="queue">Bounded in-memory channel drained by <see cref="AuditDrainer"/>.</param>
    /// <param name="clock">UTC clock; captured at enqueue time so the eventual row has the true event instant.</param>
    /// <param name="logger">Structured logger; receives the overflow diagnostic.</param>
    public AuditService(
        AuditWriteQueue queue,
        ICnasTimeProvider clock,
        ILogger<AuditService> logger)
    {
        _queue = queue;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result> RecordAsync(
        string eventCode,
        AuditSeverity severity,
        string actorId,
        string? targetEntity,
        long? targetEntityId,
        string detailsJson,
        string? sourceIp,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(detailsJson);

        // SEC 044 / CLAUDE.md §5.6 — strip PII at the single write boundary so callers
        // cannot inadvertently bypass the policy by passing freeform JSON. This MUST
        // happen before enqueue so unredacted payloads never reach the in-memory channel.
        var redactedDetails = PiiRedactor.Redact(detailsJson);

        var record = new AuditEventRecord(
            EventCode: eventCode,
            Severity: severity,
            ActorId: actorId,
            TargetEntity: targetEntity,
            TargetEntityId: targetEntityId,
            DetailsJson: redactedDetails,
            SourceIp: sourceIp,
            CorrelationId: correlationId,
            EventAtUtc: _clock.UtcNow);

        if (_queue.TryEnqueue(record))
        {
            // R0040 — bump the enqueue counter AFTER the queue accepted the record so a
            // failed TryEnqueue doesn't inflate the success count. Tagless: the
            // observable rate alone is the operator signal; the dropped counter below
            // carries the cause via its `reason` tag.
            CnasMeter.AuditEnqueued.Add(1);
            return Task.FromResult(Result.Success());
        }

        // Overflow: the drainer is behind. Log loudly so ops sees the backlog; return
        // Internal so the caller's response carries a non-success signal. R0188 added
        // archive-and-replay for the drainer's flush-failure drops; the queue-full
        // path here is a different failure mode (producer outran consumer), tagged
        // accordingly on the cnas.audit.dropped counter so dashboards can chart
        // back-pressure vs. infrastructure outage separately.
        CnasMeter.AuditDropped.Add(1, new KeyValuePair<string, object?>("reason", "queue_full"));
        _logger.LogError(
            "Audit queue full; dropped event {EventCode} actor={ActorId} target={TargetEntity}/{TargetEntityId}.",
            eventCode, actorId, targetEntity, targetEntityId);
        return Task.FromResult(Result.Failure(ErrorCodes.Internal, "Audit queue is full."));
    }
}
