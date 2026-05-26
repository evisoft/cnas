using Cnas.Ps.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R0182 / SEC 042 — hosted service that periodically rebuilds the
/// <see cref="AuditPolicyResolver"/>'s in-memory snapshot from the persisted
/// <c>cnas.AuditPolicies</c> table. The CRUD service additionally invalidates the
/// snapshot synchronously after every mutation; this job is the "no operator
/// activity for a while" safety net that ensures the snapshot eventually catches
/// any drift between the DB and the in-memory view.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a hosted service, not a Quartz job.</b> The refresh is cheap (one
/// indexed SELECT plus regex compilation), runs on a short cadence, and does not
/// need the Quartz machinery (per-fire DI scope, listener manager, DLQ). A plain
/// <see cref="BackgroundService"/> with <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
/// loops is the right shape — see R0186's <c>AuditDrainer</c> for the same
/// pattern.
/// </para>
/// <para>
/// <b>Failure policy.</b> Any exception during a refresh tick is logged at
/// <c>LogError</c> and swallowed so the loop keeps running. The resolver's
/// existing snapshot remains in effect until the next successful refresh — a
/// transient DB outage degrades to "stale snapshot" rather than "no snapshot".
/// </para>
/// </remarks>
public sealed class AuditPolicyCacheRefreshJob : BackgroundService
{
    private readonly AuditPolicyResolver _resolver;
    private readonly AuditPolicyOptions _options;
    private readonly ILogger<AuditPolicyCacheRefreshJob> _logger;

    /// <summary>Constructs the refresh job with its DI dependencies.</summary>
    /// <param name="resolver">Singleton resolver whose snapshot is rebuilt by this loop.</param>
    /// <param name="options">Bound options; <see cref="AuditPolicyOptions.RefreshIntervalSeconds"/> drives the cadence.</param>
    /// <param name="logger">Structured logger for refresh-failure diagnostics.</param>
    public AuditPolicyCacheRefreshJob(
        AuditPolicyResolver resolver,
        IOptions<AuditPolicyOptions> options,
        ILogger<AuditPolicyCacheRefreshJob> logger)
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
        // Warm-load the snapshot on startup so the very first audit-event write
        // after process boot already sees any persisted policies.
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
    /// terminates silently. Logs failures at <c>LogError</c>; the existing
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "AuditPolicyCacheRefreshJob refresh failed; snapshot retained.");
        }
    }
}
