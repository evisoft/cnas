using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Kpi;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R0201 / TOR CF 20.02 — Quartz job that drives the daily KPI snapshot run.
/// Fires at 02:00 UTC with <c>snapshotDate = today - 1</c> so the most
/// recent fully-elapsed UTC day is what the dashboard surfaces. Idempotent
/// — the underlying <see cref="IKpiSnapshotService.RunForDateAsync"/>
/// upserts on the natural key, so an overlapping retry is safe.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DisallowConcurrentExecutionAttribute"/> still applies as
/// belt-and-braces — the underlying service is idempotent, but parallel
/// fires would only waste DB round-trips for no benefit.
/// </para>
/// <para>
/// Emits <c>cnas.kpi.snapshot_run{status=success|failure}</c> per fire so
/// operators chart success rate vs. failure-burst patterns on the meter.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class KpiSnapshotJob : IJob
{
    /// <summary>Stable Quartz job identity used for registration and lookups.</summary>
    public const string JobIdentity = "kpi-snapshot";

    /// <summary>Stable Quartz trigger identity paired with <see cref="JobIdentity"/>.</summary>
    public const string TriggerIdentity = "kpi-snapshot-trigger";

    /// <summary>Cron expression — daily at 02:00 UTC.</summary>
    public const string Cron = "0 0 2 * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate.</summary>
    public const string JobCode = JobScheduleProfileRegistry.KpiSnapshot;

    private readonly IServiceScopeFactory _scopes;
    private readonly ICnasTimeProvider _clock;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<KpiSnapshotJob> _logger;

    /// <summary>Constructs the snapshot job with its scope factory + clock dependencies.</summary>
    /// <param name="scopes">DI scope factory used to resolve scoped collaborators per fire.</param>
    /// <param name="clock">UTC clock — supplies the "yesterday" date.</param>
    /// <param name="peakHourGate">R2173 peak-hour gate consulted at the top of each fire.</param>
    /// <param name="logger">Structured logger.</param>
    public KpiSnapshotJob(
        IServiceScopeFactory scopes,
        ICnasTimeProvider clock,
        IPeakHourGate peakHourGate,
        ILogger<KpiSnapshotJob> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(peakHourGate);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _clock = clock;
        _peakHourGate = peakHourGate;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;

        // R2173 / TOR PSR 004 — peak-hour gate. OffPeakOnly profile defers
        // heavy KPI rollups to the configured off-peak window. The cron is
        // already 02:00 UTC so the gate is belt-and-braces for emergency
        // manual fires.
        if (await _peakHourGate.EvaluateAsync(JobCode, ct).ConfigureAwait(false) == PeakHourGateDecision.Skip)
        {
            return;
        }

        var yesterday = DateOnly.FromDateTime(_clock.UtcNow).AddDays(-1);

        using var scope = _scopes.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IKpiSnapshotService>();

        try
        {
            var result = await service.RunForDateAsync(yesterday, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                CnasMeter.KpiSnapshotRun.Add(1,
                    new KeyValuePair<string, object?>("status", "success"));
                _logger.LogInformation(
                    "KpiSnapshotJob completed for {Date}: rows={Rows} duration={DurationMs}ms",
                    yesterday, result.Value.RowsUpserted, result.Value.DurationMs);
            }
            else
            {
                CnasMeter.KpiSnapshotRun.Add(1,
                    new KeyValuePair<string, object?>("status", "failure"));
                _logger.LogWarning(
                    "KpiSnapshotJob run for {Date} returned failure {Code}: {Message}",
                    yesterday, result.ErrorCode, result.ErrorMessage);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CnasMeter.KpiSnapshotRun.Add(1,
                new KeyValuePair<string, object?>("status", "failure"));
            _logger.LogError(ex, "KpiSnapshotJob run for {Date} threw.", yesterday);
            throw;
        }
    }
}
