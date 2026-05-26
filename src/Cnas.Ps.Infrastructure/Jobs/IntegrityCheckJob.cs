using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Integrity;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Services.Integrity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R2282 / TOR SEC 036 — Quartz job that fires the row-integrity sweep on a
/// nightly cadence. Each fire creates an <see cref="IntegrityCheckRun"/>
/// row, iterates every registered <see cref="IIntegrityCheck"/>, persists
/// findings, and finalises the run with the aggregated counters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Peak-hour gate.</b> The job's profile is <c>OffPeakOnly</c>; the gate
/// emits a single <c>cnas.peak_hour.gate{decision=skip}</c> counter when the
/// fire happens during a peak hour and the job returns immediately.
/// </para>
/// <para>
/// <b>Concurrency guard.</b> <see cref="DisallowConcurrentExecutionAttribute"/>
/// keeps two fires from racing the same data set. Even if Quartz misfires,
/// the worst case is a no-op iteration.
/// </para>
/// <para>
/// <b>Failure handling.</b> Unhandled exceptions inside the check pipeline
/// are caught by <see cref="IntegrityCheckService"/> which flips the run to
/// <c>Failed</c> with the stack class+message in <c>FailureReason</c>. If a
/// failure escapes the service AS WELL the job itself emits a Critical
/// <c>INTEGRITY_CHECK.JOB_FAILED</c> audit row so the operator paging set
/// fires.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class IntegrityCheckJob : IJob
{
    /// <summary>Stable Quartz job identity used for registration and lookups.</summary>
    public const string JobIdentity = "integrity-check";

    /// <summary>Stable Quartz trigger identity paired with <see cref="JobIdentity"/>.</summary>
    public const string TriggerIdentity = "integrity-check-trigger";

    /// <summary>
    /// Cron expression — every day at 03:00 UTC, well inside the documented
    /// off-peak window (00:00..06:00 default).
    /// </summary>
    public const string Cron = "0 0 3 * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate (OffPeakOnly profile).</summary>
    public const string JobCode = JobScheduleProfileRegistry.IntegrityCheck;

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<IntegrityCheckJob> _logger;

    /// <summary>Constructs the job with its collaborators.</summary>
    /// <param name="scopes">Scope factory used to resolve scoped collaborators per fire.</param>
    /// <param name="peakHourGate">R2173 peak-hour gate consulted at the top of each fire.</param>
    /// <param name="logger">Structured logger.</param>
    public IntegrityCheckJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        ILogger<IntegrityCheckJob> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(peakHourGate);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _peakHourGate = peakHourGate;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;

        // R2173 / TOR PSR 004 — peak-hour gate. OffPeakOnly profile.
        if (await _peakHourGate.EvaluateAsync(JobCode, ct).ConfigureAwait(false) == PeakHourGateDecision.Skip)
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IIntegrityCheckService>();

        try
        {
            // The service is the one that creates the run row + persists
            // findings. Calling ExecuteRunAsync directly bypasses the
            // manual-trigger audit (Scheduled fires deserve their own audit
            // shape — Notice-severity, not Critical).
            var concrete = (IntegrityCheckService)service;
            var run = await concrete
                .ExecuteRunAsync(IntegrityCheckTriggerKind.Scheduled, actorId: "system", ct)
                .ConfigureAwait(false);

            EmitMetrics(run);

            if (run.Status == IntegrityCheckRunStatus.Failed)
            {
                await EmitFailureAuditAsync(scope.ServiceProvider, run, ct).ConfigureAwait(false);
                _logger.LogError(
                    "IntegrityCheckJob completed with status Failed (runId={RunId}, reason={Reason}).",
                    run.Id, run.FailureReason);
                return;
            }

            _logger.LogInformation(
                "IntegrityCheckJob completed (runId={RunId}, rowsScanned={Rows}, findings={Findings}).",
                run.Id, run.TotalRowsScanned, run.TotalFindings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CnasMeter.IntegrityCheckRunCompleted.Add(1,
                new KeyValuePair<string, object?>("status", "failed"));
            await EmitJobErrorAuditAsync(scope.ServiceProvider, ex, ct).ConfigureAwait(false);
            _logger.LogError(ex, "IntegrityCheckJob crashed before the service could finalise the run.");
        }
    }

    /// <summary>Emits the per-run + per-severity counter increments.</summary>
    /// <param name="run">Finalised run row.</param>
    private static void EmitMetrics(IntegrityCheckRun run)
    {
        var status = run.Status == IntegrityCheckRunStatus.Completed ? "completed" : "failed";
        CnasMeter.IntegrityCheckRunCompleted.Add(1,
            new KeyValuePair<string, object?>("status", status));
        CnasMeter.IntegrityCheckRowsScanned.Add(run.TotalRowsScanned);

        if (string.IsNullOrWhiteSpace(run.FindingsBySeverity))
        {
            return;
        }
        try
        {
            var byKind = JsonSerializer.Deserialize<Dictionary<string, int>>(run.FindingsBySeverity);
            if (byKind is null)
            {
                return;
            }
            foreach (var kv in byKind)
            {
                if (kv.Value > 0)
                {
                    CnasMeter.IntegrityCheckFindingsRecorded.Add(kv.Value,
                        new KeyValuePair<string, object?>("severity", kv.Key));
                }
            }
        }
        catch (JsonException)
        {
            // Defensive — never throw out to Quartz from a counter emission.
        }
    }

    /// <summary>
    /// Writes the Critical <c>INTEGRITY_CHECK.JOB_FAILED</c> audit row when
    /// the service finalised the run with <see cref="IntegrityCheckRunStatus.Failed"/>.
    /// </summary>
    /// <param name="sp">DI scope's service provider.</param>
    /// <param name="run">The failed run row.</param>
    /// <param name="ct">Cancellation propagated from the fire.</param>
    private static async Task EmitFailureAuditAsync(IServiceProvider sp, IntegrityCheckRun run, CancellationToken ct)
    {
        var audit = sp.GetRequiredService<IAuditService>();
        var details = JsonSerializer.Serialize(new
        {
            runId = run.Id,
            failureReason = run.FailureReason,
            totalRowsScanned = run.TotalRowsScanned,
            totalFindings = run.TotalFindings,
        });
        await audit.RecordAsync(
            "INTEGRITY_CHECK.JOB_FAILED",
            AuditSeverity.Critical,
            actorId: "system",
            targetEntity: nameof(IntegrityCheckRun),
            targetEntityId: run.Id,
            detailsJson: details,
            sourceIp: null,
            correlationId: null,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the Critical <c>INTEGRITY_CHECK.JOB_FAILED</c> audit row when
    /// the job crashed before the service could finalise the run (rare —
    /// usually a DI / wiring fault).
    /// </summary>
    /// <param name="sp">DI scope's service provider.</param>
    /// <param name="exception">Unhandled exception.</param>
    /// <param name="ct">Cancellation propagated from the fire.</param>
    private static async Task EmitJobErrorAuditAsync(IServiceProvider sp, Exception exception, CancellationToken ct)
    {
        try
        {
            var audit = sp.GetRequiredService<IAuditService>();
            var details = JsonSerializer.Serialize(new
            {
                exceptionType = exception.GetType().Name,
                message = exception.Message,
            });
            await audit.RecordAsync(
                "INTEGRITY_CHECK.JOB_FAILED",
                AuditSeverity.Critical,
                actorId: "system",
                targetEntity: nameof(IntegrityCheckRun),
                targetEntityId: null,
                detailsJson: details,
                sourceIp: null,
                correlationId: null,
                ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Defence-in-depth — never throw out to Quartz from an audit emit.
        }
    }
}
