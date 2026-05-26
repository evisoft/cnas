using Cnas.Ps.Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R0183 / SEC 043 — hosted service that periodically rebuilds the
/// <see cref="AuditFieldPolicyResolver"/>'s in-memory snapshot from the persisted
/// <c>cnas.AuditFieldPolicies</c> table. Mirrors the R0182
/// <c>AuditPolicyCacheRefreshJob</c> shape: cheap indexed SELECT, plain
/// <see cref="BackgroundService"/> with <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
/// loops, exceptions logged and swallowed so the loop never terminates silently.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a hosted service, not a Quartz job.</b> The refresh is cheap (one
/// indexed SELECT), runs on a short cadence, and does not need the Quartz
/// machinery (per-fire DI scope, listener manager, DLQ).
/// </para>
/// <para>
/// <b>Failure policy.</b> Any exception during a refresh tick is logged at
/// <c>LogError</c> and swallowed. The resolver's existing snapshot remains in
/// effect until the next successful refresh.
/// </para>
/// </remarks>
public sealed class AuditFieldPolicyCacheRefreshJob : BackgroundService
{
    private readonly AuditFieldPolicyResolver _resolver;
    private readonly AuditFieldPolicyOptions _options;
    private readonly ILogger<AuditFieldPolicyCacheRefreshJob> _logger;

    /// <summary>Constructs the refresh job with its DI dependencies.</summary>
    /// <param name="resolver">Singleton resolver whose snapshot is rebuilt by this loop.</param>
    /// <param name="options">Bound options; <see cref="AuditFieldPolicyOptions.RefreshIntervalSeconds"/> drives the cadence.</param>
    /// <param name="logger">Structured logger for refresh-failure diagnostics.</param>
    public AuditFieldPolicyCacheRefreshJob(
        AuditFieldPolicyResolver resolver,
        IOptions<AuditFieldPolicyOptions> options,
        ILogger<AuditFieldPolicyCacheRefreshJob> logger)
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
        // Warm-load the snapshot on startup so the very first diff-write after
        // boot already sees any persisted policies.
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
    /// terminates silently.
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
            _logger.LogError(ex, "AuditFieldPolicyCacheRefreshJob refresh failed; snapshot retained.");
        }
    }
}
