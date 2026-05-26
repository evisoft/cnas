using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Reports;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R0583 / TOR CF 09.06 / CF 09.09 — Quartz job that drives the background
/// report runner. Fires every 60 seconds and calls
/// <see cref="IReportJobRunner.RunBatchAsync"/> with <c>maxJobs=10</c>. The
/// runner picks the oldest <c>Queued</c> rows in turn, runs each through the
/// engine, persists the export bytes via the R0227 attachment subsystem, and
/// notifies the requester.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DisallowConcurrentExecutionAttribute"/> ensures only one
/// instance of the job is in flight per scheduler — combined with the
/// <c>OrderBy(QueuedAtUtc)</c> pickup, this provides FIFO drainage. Idempotent
/// at the row level: once a row flips to <c>Running</c> no other instance
/// will see it as <c>Queued</c>.
/// </para>
/// <para>
/// Emits <c>cnas.report_job.run{outcome=success|failure}</c> per fire so
/// operators chart per-tick success rate vs. failure bursts on the meter.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class ReportJobBackgroundJob : IJob
{
    /// <summary>Stable Quartz job identity used for registration and lookups.</summary>
    public const string JobIdentity = "report-job-runner";

    /// <summary>Stable Quartz trigger identity paired with <see cref="JobIdentity"/>.</summary>
    public const string TriggerIdentity = "report-job-runner-trigger";

    /// <summary>
    /// Cron expression — every minute on the second boundary
    /// (<c>0 0/1 * * * ?</c>). Mirrors the cadence used by the SIEM forwarder
    /// (<c>SiemForwarderJob.Cron</c>) so the per-tick load profile is uniform
    /// across the minute-cadence jobs.
    /// </summary>
    public const string Cron = "0 0/1 * * * ?";

    /// <summary>Per-tick batch cap — drains up to this many queued jobs per fire.</summary>
    public const int BatchSize = 10;

    /// <summary>R2173 — stable job code consulted by the peak-hour gate (OffPeakOnly profile).</summary>
    public const string JobCode = JobScheduleProfileRegistry.ReportJobBackground;

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<ReportJobBackgroundJob> _logger;

    /// <summary>
    /// Constructs the background job with its scope factory and logger.
    /// </summary>
    /// <param name="scopes">DI scope factory — a fresh scope is created per fire so the runner gets a per-tick <c>ICnasDbContext</c>.</param>
    /// <param name="peakHourGate">R2173 peak-hour gate consulted at the top of each fire.</param>
    /// <param name="logger">Structured logger.</param>
    public ReportJobBackgroundJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        ILogger<ReportJobBackgroundJob> logger)
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

        // R2173 / TOR PSR 004 — peak-hour gate. OffPeakOnly profile defers
        // report rendering to the configured off-peak window to keep operator
        // UX responsive during business hours. Citizens whose reports queue
        // during the day see them resolve overnight.
        if (await _peakHourGate.EvaluateAsync(JobCode, ct).ConfigureAwait(false) == PeakHourGateDecision.Skip)
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IReportJobRunner>();

        try
        {
            var drained = await runner.RunBatchAsync(BatchSize, ct).ConfigureAwait(false);
            CnasMeter.ReportJobRun.Add(1,
                new KeyValuePair<string, object?>("outcome", "success"));
            if (drained > 0)
            {
                _logger.LogInformation(
                    "ReportJobBackgroundJob drained {Drained} job(s) on fire {FireInstanceId}.",
                    drained, context.FireInstanceId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CnasMeter.ReportJobRun.Add(1,
                new KeyValuePair<string, object?>("outcome", "failure"));
            _logger.LogError(ex, "ReportJobBackgroundJob fire {FireInstanceId} threw.", context.FireInstanceId);
            throw;
        }
    }
}
