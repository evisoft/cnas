using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// Quartz job that periodically polls newly persisted <see cref="Cnas.Ps.Core.Domain.AuditLog"/>
/// rows, formats them as ArcSight CEF lines via <see cref="ISiemExporter"/>, and writes
/// the resulting CEF traffic to a configured syslog endpoint. R0190 / SEC 049.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cadence.</b> The job is fired by the cron registered in
/// <see cref="QuartzComposition"/> (currently <c>0 */1 * * * ?</c> = every minute).
/// <see cref="SiemExporterOptions.Cron"/> is the documented seam for a future
/// per-environment override; today the cron is hard-coded at registration time so
/// the wiring does not need to resolve <see cref="IOptions{T}"/> while building the
/// scheduler.
/// </para>
/// <para>
/// <b>Checkpoint.</b> The job reads <see cref="ICnasDbContext.SiemForwarderStates"/>
/// for the singleton row (<see cref="SingletonKey"/>) and queries audit rows with
/// <see cref="Cnas.Ps.Core.Domain.AuditableEntity.Id"/> strictly greater than the
/// stored checkpoint. The forward attempt either advances the checkpoint to the
/// highest id in the batch (on success) or leaves it untouched (on failure) so a
/// transient SIEM outage results in at-least-once delivery rather than data loss.
/// </para>
/// <para>
/// <b>Disabled state.</b> When <see cref="SiemExporterOptions.Enabled"/> is
/// <c>false</c> (the default) the job returns immediately without touching the
/// database. This is the production default — the chart ships safely without
/// requiring operators to configure SIEM until they actually wire one up.
/// </para>
/// <para>
/// <b>Concurrency guard.</b> <see cref="DisallowConcurrentExecutionAttribute"/>
/// prevents two fires from racing the same checkpoint row; even if Quartz
/// misconfigures the cadence the worst-case is a missed iteration, never a
/// double-forward.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class SiemForwarderJob : IJob
{
    /// <summary>Stable Quartz job identity — referenced by <see cref="QuartzComposition"/>.</summary>
    public const string JobIdentity = "siem-forwarder";

    /// <summary>Stable Quartz trigger identity — referenced by <see cref="QuartzComposition"/>.</summary>
    public const string TriggerIdentity = "siem-forwarder-trigger";

    /// <summary>
    /// Singleton-row key used to locate the checkpoint in
    /// <see cref="ICnasDbContext.SiemForwarderStates"/>. The migration seeds exactly
    /// one row with this key; the job is hard-coded to read and write that one row.
    /// </summary>
    public const string SingletonKey = "default";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate (Always profile).</summary>
    public const string JobCode = JobScheduleProfileRegistry.SiemForwarder;

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly SiemExporterOptions _options;
    private readonly ILogger<SiemForwarderJob> _logger;

    /// <summary>
    /// Constructs the forwarder job with its scope factory + options snapshot. A scope
    /// factory is injected (rather than the scoped collaborators directly) because the
    /// job's owning scheduler is a singleton — we must materialise a fresh DI scope per
    /// fire to resolve <see cref="ICnasDbContext"/> and <see cref="ISiemExporter"/>.
    /// </summary>
    /// <param name="scopes">Scope factory used to resolve scoped collaborators per iteration.</param>
    /// <param name="peakHourGate">R2173 peak-hour gate consulted at the top of each fire.</param>
    /// <param name="options">Bound options snapshot from <see cref="SiemExporterOptions.SectionName"/>.</param>
    /// <param name="logger">Structured logger for informational + warning lines.</param>
    public SiemForwarderJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        IOptions<SiemExporterOptions> options,
        ILogger<SiemForwarderJob> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(peakHourGate);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _peakHourGate = peakHourGate;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // R2173 / TOR PSR 004 — peak-hour gate. Always profile means the gate
        // always allows; the uniform call keeps the counter time-series complete.
        if (await _peakHourGate.EvaluateAsync(JobCode, context.CancellationToken).ConfigureAwait(false)
            == PeakHourGateDecision.Skip)
        {
            return;
        }

        // Disabled-state short-circuit. Production default; lets the chart ship the job
        // registration without forcing operators to configure a SIEM endpoint.
        if (!_options.Enabled)
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICnasDbContext>();
        var exporter = scope.ServiceProvider.GetRequiredService<ISiemExporter>();
        var clock = scope.ServiceProvider.GetRequiredService<ICnasTimeProvider>();

        var state = await db.SiemForwarderStates
            .FirstOrDefaultAsync(
                s => s.Key == SingletonKey && s.IsActive,
                context.CancellationToken)
            .ConfigureAwait(false);

        if (state is null)
        {
            // The migration seeds the row; missing state means either the migration
            // has not run or an operator soft-deleted the row. Either way we cannot
            // safely advance the checkpoint, so log and skip.
            _logger.LogWarning(
                "SiemForwarderJob: state row missing (key={Key}); skipping iteration.",
                SingletonKey);
            return;
        }

        // Strictly greater-than so an already-forwarded row is never re-emitted. The
        // batch size cap bounds the DB scan so a pathological backlog cannot wedge a
        // single fire for minutes at a time.
        var batchSize = Math.Max(1, _options.BatchSize);
        var rows = await db.AuditLogs
            .Where(a => a.IsActive && a.Id > state.LastForwardedAuditId)
            .OrderBy(a => a.Id)
            .Take(batchSize)
            .ToListAsync(context.CancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return;
        }

        var forwardResult = await exporter
            .ForwardAsync(rows, context.CancellationToken)
            .ConfigureAwait(false);

        if (forwardResult.IsFailure)
        {
            // Pin the checkpoint so the next iteration retries the same range.
            _logger.LogWarning(
                "SiemForwarderJob: exporter failed ({Code}); checkpoint not advanced.",
                forwardResult.ErrorCode);
            return;
        }

        // R0190 invariant — the checkpoint advances to the highest id in the batch
        // (NOT only the rows that passed the min-severity filter). Severity filtering
        // is the exporter's concern; the checkpoint tracks "rows we have considered",
        // not "rows the SIEM ingested". Otherwise a long run of below-threshold rows
        // would force us to re-scan them on every iteration.
        var maxId = rows.Max(r => r.Id);
        state.LastForwardedAuditId = maxId;
        state.LastForwardedAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);

        CnasMeter.SiemForwarded.Add(rows.Count);
        _logger.LogInformation(
            "SiemForwarderJob forwarded {Count} audit rows; checkpoint advanced to {MaxId}.",
            rows.Count,
            maxId);
    }
}
