using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.E2E.Tests;

/// <summary>
/// Test-only <see cref="IAuditService"/> that writes synchronously, bypassing the
/// production async <c>AuditService</c> + <c>AuditDrainer</c> pipeline introduced by
/// R0186. E2E journeys assert audit-log presence immediately after the HTTP call; the
/// async pipeline would race those assertions because the drainer runs on a separate
/// background loop.
/// </summary>
/// <remarks>
/// <para>
/// The async pipeline is independently covered by the unit tests in
/// <c>Cnas.Ps.Infrastructure.Tests.Services.AuditDrainerTests</c>, so swapping it out
/// for the E2E suite does not erode the test envelope — it just keeps the journey
/// assertions deterministic.
/// </para>
/// <para>
/// PII redaction continues to run at the head of <see cref="RecordAsync"/>, matching
/// the production service so the SEC 044 / CLAUDE.md §5.6 contract is identical.
/// </para>
/// </remarks>
public sealed class SynchronousAuditService : IAuditService
{
    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly IMLogClient _mlog;

    /// <summary>Constructs the synchronous audit writer.</summary>
    /// <param name="db">Per-request DbContext (scoped).</param>
    /// <param name="clock">UTC clock.</param>
    /// <param name="mlog">MLog client used to mirror Critical events.</param>
    public SynchronousAuditService(ICnasDbContext db, ICnasTimeProvider clock, IMLogClient mlog)
    {
        _db = db;
        _clock = clock;
        _mlog = mlog;
    }

    /// <inheritdoc />
    public async Task<Result> RecordAsync(
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

        detailsJson = PiiRedactor.Redact(detailsJson);

        var now = _clock.UtcNow;
        var record = new AuditEventRecord(
            EventCode: eventCode,
            Severity: severity,
            ActorId: actorId,
            TargetEntity: targetEntity,
            TargetEntityId: targetEntityId,
            DetailsJson: detailsJson,
            SourceIp: sourceIp,
            CorrelationId: correlationId,
            EventAtUtc: now);

        // R0194 / SEC 047 — chain this row from the current tail so the verifier
        // accepts E2E-seeded data. We use the same projector helper as the
        // production drainer so the canonical form cannot drift.
        var prev = await _db.AuditLogs
            .OrderByDescending(a => a.Id)
            .Select(a => a.RowHash)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? "GENESIS";
        var row = AuditFlushProjector.ToAuditLog(record);
        row.PrevHash = prev;
        row.RowHash = AuditFlushProjector.ComputeRowHash(record, prev);
        _db.AuditLogs.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (severity == AuditSeverity.Critical)
        {
            await _mlog.AppendAsync(
                new MLogEntry(eventCode, actorId, targetEntity, targetEntityId, detailsJson),
                cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }
}
