using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// Quartz job that periodically retries audit batches whose primary flush failed
/// and were spilled to <see cref="IAuditArchive"/>. R0188.
/// </summary>
/// <remarks>
/// <para>
/// Cadence: every 5 minutes (see <see cref="QuartzComposition"/>). Each run lists
/// the pending archives, reads each batch, opens a fresh DI scope, and re-attempts
/// the same DB + MLog projection performed by <see cref="AuditDrainer"/>
/// (via the shared <see cref="AuditFlushProjector"/> helper — drift between the
/// two paths would silently corrupt the audit story).
/// </para>
/// <para>
/// A successful replay deletes the archive. A failed replay leaves the file in
/// place for the next run; this iteration does not track per-batch retry counts —
/// persistent failures are operator-investigated. The
/// <see cref="AuditArchiveOptions.MaxReplayBatchesPerRun"/> cap protects against
/// a pathological backlog wedging a single run for hours.
/// </para>
/// <para>
/// <see cref="DisallowConcurrentExecutionAttribute"/> prevents a second fire from
/// racing the same archive ids; the underlying archive is also race-safe (read
/// tolerates concurrent delete; delete tolerates concurrent delete) so even a
/// missing guard would only produce a redundant retry, never corruption.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class AuditArchiveReplayJob : IJob
{
    /// <summary>R2173 — stable job code consulted by the peak-hour gate (OffPeakOnly profile).</summary>
    public const string JobCode = JobScheduleProfileRegistry.AuditArchiveReplay;

    private readonly IAuditArchive _archive;
    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly AuditArchiveOptions _options;
    private readonly ILogger<AuditArchiveReplayJob> _logger;

    /// <summary>Constructs the replay job with its singleton + scope-factory dependencies.</summary>
    /// <param name="archive">Durable spill area to drain.</param>
    /// <param name="scopes">Scope factory used to resolve scoped collaborators per replay.</param>
    /// <param name="peakHourGate">R2173 peak-hour gate consulted at the top of each fire.</param>
    /// <param name="options">Replay limits snapshot.</param>
    /// <param name="logger">Structured logger.</param>
    public AuditArchiveReplayJob(
        IAuditArchive archive,
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        IOptions<AuditArchiveOptions> options,
        ILogger<AuditArchiveReplayJob> logger)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(peakHourGate);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _archive = archive;
        _scopes = scopes;
        _peakHourGate = peakHourGate;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // R2173 / TOR PSR 004 — peak-hour gate. OffPeakOnly profile defers
        // audit-archive replays to off-peak so the drainer-retry path does not
        // contend with operator-facing services during business hours.
        if (await _peakHourGate.EvaluateAsync(JobCode, context.CancellationToken).ConfigureAwait(false)
            == PeakHourGateDecision.Skip)
        {
            return;
        }

        var pending = await _archive.ListPendingAsync(context.CancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return;
        }

        var attempted = 0;
        var succeeded = 0;
        var maxPerRun = Math.Max(1, _options.MaxReplayBatchesPerRun);

        foreach (var batchRef in pending.Take(maxPerRun))
        {
            attempted++;
            var records = await _archive
                .ReadAsync(batchRef.Id, context.CancellationToken)
                .ConfigureAwait(false);

            if (records.Count == 0)
            {
                // Vanished concurrently, or quarantined as corrupt — either way
                // there is nothing for us to replay; sweep the empty marker.
                await _archive.DeleteAsync(batchRef.Id, context.CancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ICnasDbContext>();
                var mlog = scope.ServiceProvider.GetRequiredService<IMLogClient>();

                // R0194 / SEC 047 — mirror the drainer's chain discipline so a
                // replayed batch slots into the chain at the current tail
                // rather than restarting at GENESIS. We sort by EventAtUtc so
                // the chained order reflects the true business-event order,
                // exactly as the live drainer does — the two write paths share
                // AuditFlushProjector.ComputeRowHash so they cannot drift.
                var ordered = records.OrderBy(r => r.EventAtUtc).ToList();
                var prev = await db.AuditLogs
                    .OrderByDescending(a => a.Id)
                    .Select(a => a.RowHash)
                    .FirstOrDefaultAsync(context.CancellationToken)
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
                await db.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);

                foreach (var r in ordered)
                {
                    await mlog.AppendAsync(
                        AuditFlushProjector.ToMLogEntry(r),
                        context.CancellationToken).ConfigureAwait(false);
                }

                await _archive.DeleteAsync(batchRef.Id, context.CancellationToken).ConfigureAwait(false);
                succeeded++;
                // R0040 — successful replay; counts per file so dashboards can chart
                // the rate at which the backlog drains.
                CnasMeter.AuditReplaySucceeded.Add(1);
                _logger.LogInformation(
                    "Replayed audit batch {Id} ({Count} records).",
                    batchRef.Id, records.Count);
            }
            catch (Exception ex)
            {
                // R0040 — failed replay; counts per file so dashboards can chart the
                // rate at which the backlog is failing to drain (e.g. persistent DB
                // outage). The file stays on disk for the next iteration.
                CnasMeter.AuditReplayFailed.Add(1);
                _logger.LogError(
                    ex,
                    "Replay failed for audit batch {Id}; leaving on disk for next run.",
                    batchRef.Id);
            }
        }

        // R0040 — record one "attempted" tick per iteration that touched ≥1 archive
        // file. Iterations with no pending archives (the no-op early-return above)
        // are NOT counted; the rate of cnas.audit.replay.attempted is the operator
        // signal for "the replay job is actively processing a backlog".
        if (attempted > 0)
        {
            CnasMeter.AuditReplayAttempted.Add(1);
        }

        _logger.LogInformation(
            "AuditArchiveReplayJob attempted {Attempted} of {Pending} batches; {Succeeded} succeeded.",
            attempted, pending.Count, succeeded);
    }
}
