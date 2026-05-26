using Cnas.Ps.Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R0128 / R0173 — hosted service that periodically rebuilds the
/// <see cref="WorkflowNotificationStrategyResolver"/>'s in-memory snapshot from the
/// persisted <c>cnas.WorkflowNotificationStrategies</c> table. The CRUD service
/// additionally invalidates the snapshot synchronously after every mutation; this job
/// is the safety net that picks up out-of-band changes (direct SQL, replication catch-
/// up, etc.) within one refresh tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a hosted service, not a Quartz job.</b> The refresh is cheap (one indexed
/// SELECT), runs on a short cadence, and does not need the Quartz machinery (per-fire
/// DI scope, listener manager, DLQ). A plain <see cref="BackgroundService"/> mirrors
/// the R0182 <c>AuditPolicyCacheRefreshJob</c> shape.
/// </para>
/// <para>
/// <b>Failure policy.</b> Any exception during a refresh tick is logged at
/// <see cref="LogLevel.Error"/> and swallowed so the loop keeps running. The
/// resolver's existing snapshot remains in effect until the next successful refresh —
/// a transient DB outage degrades to "stale snapshot" rather than "no snapshot".
/// </para>
/// </remarks>
public sealed class WorkflowNotificationStrategyCacheRefreshJob : BackgroundService
{
    private readonly WorkflowNotificationStrategyResolver _resolver;
    private readonly WorkflowNotificationStrategyOptions _options;
    private readonly ILogger<WorkflowNotificationStrategyCacheRefreshJob> _logger;

    /// <summary>Constructs the refresh job with its DI dependencies.</summary>
    /// <param name="resolver">Singleton resolver whose snapshot is rebuilt by this loop.</param>
    /// <param name="options">Bound options; <see cref="WorkflowNotificationStrategyOptions.RefreshIntervalSeconds"/> drives the cadence.</param>
    /// <param name="logger">Structured logger for refresh-failure diagnostics.</param>
    public WorkflowNotificationStrategyCacheRefreshJob(
        WorkflowNotificationStrategyResolver resolver,
        IOptions<WorkflowNotificationStrategyOptions> options,
        ILogger<WorkflowNotificationStrategyCacheRefreshJob> logger)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _resolver = resolver;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Warm-load the snapshot on startup so the very first workflow dispatch after
        // process boot already sees any persisted strategies.
        await SafeRefreshAsync(stoppingToken).ConfigureAwait(false);

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.RefreshIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            await SafeRefreshAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Performs one refresh attempt and swallows any exception so the loop never
    /// terminates silently. Logs failures at <see cref="LogLevel.Error"/>; the existing
    /// snapshot remains in effect until the next attempt succeeds.
    /// </summary>
    /// <param name="ct">Cancellation token observed during the refresh.</param>
    private async Task SafeRefreshAsync(CancellationToken ct)
    {
        try
        {
            await _resolver.InvalidateAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — let the caller observe the cancellation.
        }
#pragma warning disable CA1031 // Best-effort refresh: must not crash the host on a transient DB outage.
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "WorkflowNotificationStrategyCacheRefreshJob refresh failed; snapshot retained.");
        }
#pragma warning restore CA1031
    }
}
