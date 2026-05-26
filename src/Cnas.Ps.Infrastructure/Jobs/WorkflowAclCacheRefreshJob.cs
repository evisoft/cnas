using Cnas.Ps.Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R0126 / CF 16.10 — hosted service that periodically rebuilds the
/// <see cref="WorkflowAclService"/>'s in-memory ACL snapshots from the persisted
/// <c>cnas.WorkflowDefinitions</c> + <c>cnas.WorkflowStepAcls</c> tables. The CRUD
/// service additionally invalidates the snapshot synchronously after every mutation;
/// this job is the "no operator activity for a while" safety net that ensures the
/// snapshot eventually catches any drift between the DB and the in-memory view.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a hosted service, not a Quartz job.</b> Mirrors the R0182
/// <c>AuditPolicyCacheRefreshJob</c> shape — the refresh is cheap, runs on a short
/// cadence, and does not need the Quartz machinery (per-fire DI scope, listener
/// manager, DLQ). A plain <see cref="BackgroundService"/> with
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> loops is the right shape.
/// </para>
/// <para>
/// <b>Failure policy.</b> Any exception during a refresh tick is logged at
/// <c>LogError</c> and swallowed so the loop keeps running. The resolver's existing
/// snapshot remains in effect until the next successful refresh — a transient DB
/// outage degrades to "stale snapshot" rather than "no snapshot".
/// </para>
/// </remarks>
public sealed class WorkflowAclCacheRefreshJob : BackgroundService
{
    private readonly WorkflowAclService _resolver;
    private readonly WorkflowAclOptions _options;
    private readonly ILogger<WorkflowAclCacheRefreshJob> _logger;

    /// <summary>Constructs the refresh job with its DI dependencies.</summary>
    /// <param name="resolver">Singleton resolver whose snapshot is rebuilt by this loop.</param>
    /// <param name="options">Bound options; <see cref="WorkflowAclOptions.RefreshIntervalSeconds"/> drives the cadence.</param>
    /// <param name="logger">Structured logger for refresh-failure diagnostics.</param>
    public WorkflowAclCacheRefreshJob(
        WorkflowAclService resolver,
        IOptions<WorkflowAclOptions> options,
        ILogger<WorkflowAclCacheRefreshJob> logger)
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
        // Warm-load on startup so the very first ACL check after process boot already
        // sees any persisted rows.
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
    /// terminates silently. Logs failures at <c>LogError</c>; the existing snapshot
    /// remains in effect until the next attempt succeeds.
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "WorkflowAclCacheRefreshJob refresh failed; snapshot retained.");
        }
    }
}
