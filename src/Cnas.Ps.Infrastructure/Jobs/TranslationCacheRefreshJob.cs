using Cnas.Ps.Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R0210 / TOR UI 007 / CF 17.16 — hosted service that periodically rebuilds the
/// <see cref="TranslationResolver"/>'s in-memory snapshot from the persisted
/// <c>cnas.TranslationKeys</c> + <c>cnas.TranslationValues</c> tables. The CRUD
/// value-side service additionally invalidates the snapshot synchronously after
/// every mutation; this job is the safety net that picks up out-of-band changes
/// within one refresh tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>Failure policy.</b> Any exception during a refresh tick is logged at
/// <see cref="LogLevel.Error"/> and swallowed so the loop keeps running. The
/// existing snapshot remains in effect — a transient DB outage degrades to "stale
/// snapshot" rather than "no snapshot".
/// </para>
/// </remarks>
public sealed class TranslationCacheRefreshJob : BackgroundService
{
    private readonly TranslationResolver _resolver;
    private readonly TranslationOptions _options;
    private readonly ILogger<TranslationCacheRefreshJob> _logger;

    /// <summary>Constructs the refresh job with its DI dependencies.</summary>
    /// <param name="resolver">Singleton resolver whose snapshot is rebuilt by this loop.</param>
    /// <param name="options">Bound options; <see cref="TranslationOptions.RefreshIntervalSeconds"/> drives the cadence.</param>
    /// <param name="logger">Structured logger for refresh-failure diagnostics.</param>
    public TranslationCacheRefreshJob(
        TranslationResolver resolver,
        IOptions<TranslationOptions> options,
        ILogger<TranslationCacheRefreshJob> logger)
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
        // Warm-load on startup so the very first call after process boot already sees
        // any persisted translations.
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
    /// terminates silently. Logs failures at <see cref="LogLevel.Error"/>; the
    /// existing snapshot remains in effect until the next attempt succeeds.
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
            _logger.LogError(ex, "TranslationCacheRefreshJob refresh failed; snapshot retained.");
        }
#pragma warning restore CA1031
    }
}
