using Cnas.Ps.Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R0225 / TOR UI 015 — hosted service that periodically rebuilds the
/// <see cref="HelpResolver"/>'s in-memory snapshot from the persisted
/// <c>cnas.HelpTopics</c> + <c>cnas.HelpTopicTranslations</c> tables. Mirrors
/// <see cref="TranslationCacheRefreshJob"/> failure + cadence semantics.
/// </summary>
public sealed class HelpCacheRefreshJob : BackgroundService
{
    private readonly HelpResolver _resolver;
    private readonly HelpOptions _options;
    private readonly ILogger<HelpCacheRefreshJob> _logger;

    /// <summary>Constructs the refresh job with its DI dependencies.</summary>
    /// <param name="resolver">Singleton resolver whose snapshot is rebuilt by this loop.</param>
    /// <param name="options">Bound options; <see cref="HelpOptions.RefreshIntervalSeconds"/> drives the cadence.</param>
    /// <param name="logger">Structured logger for refresh-failure diagnostics.</param>
    public HelpCacheRefreshJob(
        HelpResolver resolver,
        IOptions<HelpOptions> options,
        ILogger<HelpCacheRefreshJob> logger)
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

    /// <summary>Performs one refresh attempt and swallows transient failures.</summary>
    /// <param name="ct">Cancellation token observed during the refresh.</param>
    private async Task SafeRefreshAsync(CancellationToken ct)
    {
        try
        {
            await _resolver.InvalidateAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown.
        }
#pragma warning disable CA1031 // Best-effort refresh: must not crash the host on a transient DB outage.
        catch (Exception ex)
        {
            _logger.LogError(ex, "HelpCacheRefreshJob refresh failed; snapshot retained.");
        }
#pragma warning restore CA1031
    }
}
