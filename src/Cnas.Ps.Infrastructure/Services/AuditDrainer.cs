using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Hosted background service that drains <see cref="AuditWriteQueue"/> and writes the
/// queued events to the local AuditLog table + mirrors every event to MLog. R0186.
/// </summary>
/// <remarks>
/// <para>
/// Flush trigger: whichever of these happens first —
/// <list type="bullet">
/// <item><see cref="FlushBatchSize"/> (50) records have accumulated; or</item>
/// <item><see cref="FlushInterval"/> (1 second) has elapsed since the first buffered record.</item>
/// </list>
/// </para>
/// <para>
/// Failure policy (R0188): any exception during flush is logged at <c>LogError</c>
/// AND the batch is spilled to the durable <see cref="IAuditArchive"/>. A periodic
/// <c>AuditArchiveReplayJob</c> retries the spilled batches until they flush
/// cleanly. The drainer itself does NOT block-retry — a wedged drainer would back up
/// the queue and start dropping new records; the archive is the durability story.
/// </para>
/// <para>
/// Lifetimes: <see cref="AuditWriteQueue"/> is a singleton (shared with every scoped
/// <see cref="AuditService"/>); <see cref="ICnasDbContext"/> and <see cref="IMLogClient"/>
/// are scoped, so each flush opens a fresh <see cref="IServiceScope"/>.
/// </para>
/// <para>
/// Graceful shutdown: when <c>stoppingToken</c> is signalled the run loop exits and a
/// final best-effort flush drains any remaining records — failures during shutdown
/// are also archived for the next process start's replay.
/// </para>
/// </remarks>
public sealed class AuditDrainer : BackgroundService
{
    /// <summary>Maximum number of records flushed in a single batch.</summary>
    internal const int FlushBatchSize = 50;

    /// <summary>Maximum time to wait between flushes when the buffer is below <see cref="FlushBatchSize"/>.</summary>
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly AuditWriteQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly IAuditArchive _archive;
    private readonly ILogger<AuditDrainer> _logger;

    /// <summary>Constructs the drainer with its singleton + scope-factory dependencies.</summary>
    /// <param name="queue">Bounded queue producing the records to drain.</param>
    /// <param name="scopes">Scope factory used to resolve scoped collaborators per flush.</param>
    /// <param name="archive">Durable spill area used when a flush fails (R0188).</param>
    /// <param name="logger">Structured logger.</param>
    public AuditDrainer(
        AuditWriteQueue queue,
        IServiceScopeFactory scopes,
        IAuditArchive archive,
        ILogger<AuditDrainer> logger)
    {
        _queue = queue;
        _scopes = scopes;
        _archive = archive;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FlushOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Defensive: FlushOnceAsync already swallows its own flush errors. This
                // catch covers anything outside the inner try (e.g. a Reader.ReadAsync
                // throwing) so the background loop never terminates silently.
                _logger.LogError(ex, "AuditDrainer outer loop caught an unexpected exception; continuing.");
            }
        }

        // Graceful shutdown — drain whatever is still queued, once, with cancellation
        // already triggered. CancellationToken.None so the inner reads don't bail on
        // the same signal that woke us up.
        try
        {
            await DrainRemainingAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AuditDrainer final shutdown flush failed; remaining records dropped.");
        }
    }

    /// <summary>
    /// Drives one full flush cycle: reads from the queue (blocking up to
    /// <see cref="FlushInterval"/>), accumulates up to <see cref="FlushBatchSize"/>
    /// records, then writes the batch to the DB + MLog. Returns immediately when the
    /// queue yields no records before the cancellation token fires.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token observed during reads.</param>
    /// <remarks>
    /// Marked <c>internal</c> so tests can drive a single cycle deterministically
    /// without running the BackgroundService loop. Production callers go through
    /// <see cref="ExecuteAsync"/>.
    /// </remarks>
    internal async Task FlushOnceAsync(CancellationToken stoppingToken)
    {
        var buffer = new List<AuditEventRecord>(FlushBatchSize);

        // Wait for at least one record OR cancellation.
        try
        {
            var first = await _queue.Reader.ReadAsync(stoppingToken).ConfigureAwait(false);
            buffer.Add(first);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        // Drain anything already queued, up to the batch limit.
        while (buffer.Count < FlushBatchSize && _queue.Reader.TryRead(out var more))
        {
            buffer.Add(more);
        }

        // If we're still under the batch limit, wait up to FlushInterval for more.
        if (buffer.Count < FlushBatchSize)
        {
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            delayCts.CancelAfter(FlushInterval);
            try
            {
                while (buffer.Count < FlushBatchSize)
                {
                    var next = await _queue.Reader.ReadAsync(delayCts.Token).ConfigureAwait(false);
                    buffer.Add(next);
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Timer elapsed — flush whatever we have.
            }
            catch (OperationCanceledException)
            {
                // Outer stop requested — flush what we have before exiting.
            }
        }

        await FlushAsync(buffer, stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drains every remaining record from the queue without waiting, then flushes.
    /// Invoked exactly once during graceful shutdown.
    /// </summary>
    private async Task DrainRemainingAsync()
    {
        var buffer = new List<AuditEventRecord>(FlushBatchSize);
        while (_queue.Reader.TryRead(out var leftover))
        {
            buffer.Add(leftover);
            if (buffer.Count >= FlushBatchSize)
            {
                await FlushAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                buffer.Clear();
            }
        }
        if (buffer.Count > 0)
        {
            await FlushAsync(buffer, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Persists <paramref name="batch"/> to the local AuditLog table and forwards each
    /// record to MLog. Failures are logged at <c>LogError</c> and the batch is
    /// archived via <see cref="IAuditArchive.ArchiveAsync"/> for replay (R0188).
    /// </summary>
    /// <param name="batch">Records to flush; safe to pass an empty list (no-op).</param>
    /// <param name="ct">Cancellation token observed during DB + MLog calls.</param>
    private async Task FlushAsync(IReadOnlyList<AuditEventRecord> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ICnasDbContext>();
            var mlog = scope.ServiceProvider.GetRequiredService<IMLogClient>();
            // R0182 — resolver is optional in the scope so legacy test harnesses
            // built without it continue to compile + run. Pass-through is the safe
            // default: every record keeps its caller-supplied severity and the
            // PiiRedactor default key set.
            var resolver = scope.ServiceProvider.GetService<IAuditPolicyResolver>();

            // R0182 — apply the resolver to each record BEFORE the hash chain so
            // the on-disk row reflects the operator-configured severity / extra
            // redaction, and the SHA-256 chain anchors on the final shape.
            var resolved = ApplyResolver(batch, resolver);

            // R0194 / SEC 047 — chain each new row from the previous row's hash.
            // The chain tail is the highest-Id row currently persisted; when the
            // table is empty we anchor from the literal "GENESIS". Within the
            // batch we sort by EventAtUtc (ascending) so the chain order
            // reflects the true business-event order rather than the (best-
            // effort) channel-read order.
            var ordered = resolved.OrderBy(r => r.EventAtUtc).ToList();
            if (ordered.Count == 0)
            {
                // Every record in the batch was suppressed by policy. Nothing to
                // chain, persist, or mirror. Still emit a flushed-batch counter so
                // the operator dashboard reflects the drained iteration; bucket
                // tag uses the original (unsuppressed) batch size so the chart
                // remains stable across suppression noise.
                CnasMeter.AuditFlushed.Add(1,
                    new KeyValuePair<string, object?>("batch.size_bucket", BatchSizeBucket(batch.Count)));
                return;
            }

            var prev = await db.AuditLogs
                .OrderByDescending(a => a.Id)
                .Select(a => a.RowHash)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false)
                ?? "GENESIS";

            var rows = new List<Cnas.Ps.Core.Domain.AuditLog>(ordered.Count);
            foreach (var r in ordered)
            {
                var rowHash = AuditFlushProjector.ComputeRowHash(r, prev);
                var row = AuditFlushProjector.ToAuditLog(r);
                row.PrevHash = prev;
                row.RowHash = rowHash;
                rows.Add(row);
                prev = rowHash;
            }
            db.AuditLogs.AddRange(rows);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // R0040 — record the successful flush BEFORE the MLog forward so a partial
            // MLog failure (which currently logs but does not throw) is still counted
            // as a successful flush of the local AuditLog rows. The bucket tag is
            // capped at 50 to match the drainer's batch ceiling, keeping cardinality
            // bounded irrespective of future flush-size tuning.
            CnasMeter.AuditFlushed.Add(1,
                new KeyValuePair<string, object?>("batch.size_bucket", BatchSizeBucket(batch.Count)));

            // MLog forward — sequential to preserve correlation ordering. We forward
            // EVERY event here, not just Critical: pre-R0186 the local audit row was
            // written synchronously inside the request thread, and the mirror was a
            // best-effort side-channel. Now that BOTH writes run in the drainer the
            // cost of universal mirroring is bounded by the same throughput envelope,
            // and we get a single source of truth for cross-system correlation. Per-
            // event MLog failures are tolerated by the underlying client (it converts
            // to a Result.Failure rather than throwing). Forwarded in the chained
            // order (same as ordered) so the MLog tail mirrors the local chain tail.
            foreach (var r in ordered)
            {
                await mlog.AppendAsync(
                    AuditFlushProjector.ToMLogEntry(r),
                    ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "AuditDrainer flush iteration failed; archiving {Count} records for replay.",
                batch.Count);
            // R0188 — durable archive-and-replay rather than silent drop.
            try
            {
                await _archive.ArchiveAsync(batch, CancellationToken.None).ConfigureAwait(false);
                // R0040 — count successful spills so dashboards can chart "primary
                // pipeline degraded" without the replay job's success rate masking the
                // problem upstream.
                CnasMeter.AuditArchived.Add(1);
                CnasMeter.AuditDropped.Add(batch.Count,
                    new KeyValuePair<string, object?>("reason", "flush_failed"));
            }
            catch (Exception archiveEx)
            {
                _logger.LogError(
                    archiveEx,
                    "Audit archive itself failed; {Count} records dropped on the floor.",
                    batch.Count);
                // R0040 — archive itself failed; this is the true floor-drop path. Tag
                // the cause so dashboards can distinguish from queue-full back-pressure.
                CnasMeter.AuditDropped.Add(batch.Count,
                    new KeyValuePair<string, object?>("reason", "archive_failed"));
            }
        }
    }

    /// <summary>
    /// R0182 / SEC 042 — applies the resolved <see cref="AuditPolicy"/> set to every
    /// in-flight record before the chain + persist + mirror pipeline runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per record the helper:
    /// <list type="bullet">
    ///   <item>Resolves the policy via <see cref="IAuditPolicyResolver.Resolve"/>.</item>
    ///   <item>If the policy says <c>Suppress=true</c> AND the effective severity is
    ///   <see cref="AuditSeverity.Information"/>, the record is filtered out + the
    ///   per-policy suppressed counter increments.</item>
    ///   <item>If <c>Suppress=true</c> but the effective severity is NOT Information,
    ///   the safeguard kicks in: the record is kept, a WARN log is emitted, and the
    ///   misconfig counter increments.</item>
    ///   <item>Otherwise the record is re-issued with the resolved severity and a
    ///   re-redacted <c>DetailsJson</c> that merges the policy's extra keys.</item>
    /// </list>
    /// </para>
    /// <para>
    /// When <paramref name="resolver"/> is <c>null</c> the helper is a no-op
    /// projection — every record passes through unchanged. This is the safe default
    /// when legacy test harnesses do not register the resolver.
    /// </para>
    /// </remarks>
    /// <param name="batch">In-flight batch from the queue.</param>
    /// <param name="resolver">Resolved <see cref="IAuditPolicyResolver"/> from the scope (nullable).</param>
    /// <returns>The (possibly shorter, possibly mutated) batch.</returns>
    private List<AuditEventRecord> ApplyResolver(
        IReadOnlyList<AuditEventRecord> batch,
        IAuditPolicyResolver? resolver)
    {
        var output = new List<AuditEventRecord>(batch.Count);
        if (resolver is null)
        {
            output.AddRange(batch);
            return output;
        }

        foreach (var r in batch)
        {
            var resolved = resolver.Resolve(
                r.EventCode,
                r.Severity,
                r.Module,
                r.Screen,
                r.DataCategory);

            // A resolver-internal failure must never abort the flush — the resolver
            // contract is defensive (Result-typed). Fall back to pass-through.
            if (resolved.IsFailure || resolved.Value is null)
            {
                output.Add(r);
                continue;
            }

            var p = resolved.Value;

            if (p.Suppress)
            {
                if (p.EffectiveSeverity == AuditSeverity.Information)
                {
                    // Legal suppression — drop the row.
                    CnasMeter.AuditPolicySuppressed.Add(
                        1,
                        new KeyValuePair<string, object?>("policy", p.MatchedPolicyCode ?? "unknown"));
                    continue;
                }
                // Safeguard: non-Information events MUST land in the journal.
                _logger.LogWarning(
                    "AuditPolicy {Policy} attempted to suppress non-Information event {EventCode} (severity={Severity}); overriding the suppression and keeping the row.",
                    p.MatchedPolicyCode,
                    r.EventCode,
                    p.EffectiveSeverity);
                CnasMeter.AuditPolicyMisconfig.Add(
                    1,
                    new KeyValuePair<string, object?>("policy", p.MatchedPolicyCode ?? "unknown"));
                // Fall through and persist below.
            }

            // Apply the resolved severity + extra-redact merge. The PII redactor is
            // re-invoked on the already-redacted DetailsJson — redacting twice is
            // safe (idempotent on already-redacted values, additionally redacts any
            // extra keys the policy demanded). We compute on top of the queued
            // payload rather than the original because R0185 stripped PII at
            // enqueue and we must preserve that invariant.
            var redacted = PiiRedactor.Redact(r.DetailsJson, p.ExtraRedactKeys);

            output.Add(r with
            {
                Severity = p.EffectiveSeverity,
                DetailsJson = redacted,
            });
        }
        return output;
    }

    /// <summary>
    /// Maps a batch size to the nearest equal-or-greater bucket from {1, 5, 10, 50}.
    /// Used as a low-cardinality tag value on <c>cnas.audit.flushed</c> so flush-size
    /// charts stay readable irrespective of the batch ceiling.
    /// </summary>
    /// <param name="batchCount">Number of records in the flushed batch.</param>
    /// <returns>String form of the chosen bucket (<c>"1"</c>, <c>"5"</c>, <c>"10"</c>, or <c>"50"</c>).</returns>
    private static string BatchSizeBucket(int batchCount)
        => batchCount switch
        {
            <= 1 => "1",
            <= 5 => "5",
            <= 10 => "10",
            _ => "50",
        };
}
