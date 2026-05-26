using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Observability;

/// <summary>
/// Hosted service that periodically refreshes a cached count of pending admin actions
/// not yet decided or expired. The cached value is exposed via <see cref="LastBacklog"/>
/// and read by the <c>cnas.admin.action.backlog</c> observable gauge registered with
/// <see cref="CnasMeter.RegisterAdminActionBacklogGauge"/>. R0040 follow-up.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a background updater.</b> Observable-gauge callbacks run on the OTel
/// collection thread; opening a scoped <see cref="ICnasDbContext"/> on that thread on
/// every export interval is both expensive (DB round-trip) and risky (the OTel
/// pipeline cannot tolerate exceptions in the callback). Caching the count in a
/// primitive <c>long</c> updated on a 30-second cadence keeps the gauge callback
/// allocation-free and non-blocking.
/// </para>
/// <para>
/// <b>Stale-tolerant.</b> Any exception during the refresh is swallowed and the
/// previous good value remains visible — the gauge shows the last-known backlog
/// rather than a missing data point. Operators see staleness as a flat-line on the
/// dashboard rather than gaps, which is the desired UX for a low-priority metric.
/// </para>
/// <para>
/// <b>Lifetimes.</b> Registered as both a singleton and a hosted service so the
/// gauge callback (closure captured at DI composition time) reads the same instance
/// the background loop updates. <see cref="Interlocked.Exchange(ref long, long)"/>
/// ensures the write/read pair is atomic on 32-bit hosts as well.
/// </para>
/// </remarks>
public sealed class AdminActionBacklogObserver : BackgroundService
{
    /// <summary>Refresh cadence — long enough that we don't load the read replica.</summary>
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly ICnasTimeProvider _clock;
    private readonly ILogger<AdminActionBacklogObserver> _logger;
    private long _lastBacklog;

    /// <summary>Constructs the observer with its scope factory and clock dependencies.</summary>
    /// <param name="scopes">Scope factory used to resolve <see cref="IReadOnlyCnasDbContext"/> per refresh.</param>
    /// <param name="clock">UTC clock so the "not yet expired" filter is consistent with the rest of the system.</param>
    /// <param name="logger">Structured logger.</param>
    public AdminActionBacklogObserver(
        IServiceScopeFactory scopes,
        ICnasTimeProvider clock,
        ILogger<AdminActionBacklogObserver> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Latest cached backlog count, refreshed every <see cref="RefreshInterval"/>.
    /// Reads are non-blocking and tolerate staleness — the value lags reality by at
    /// most one refresh interval plus a DB round-trip.
    /// </summary>
    public long LastBacklog => Interlocked.Read(ref _lastBacklog);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Eagerly refresh once at startup so the first gauge sample is not zero just
        // because the loop hasn't ticked yet.
        await RefreshAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RefreshInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            await RefreshAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens a fresh scope, queries the pending admin-action backlog, and writes the
    /// result atomically into the cached field. Failures are logged at <c>LogWarning</c>
    /// and the previous good value remains visible.
    /// </summary>
    /// <param name="ct">Cancellation token observed during the DB call.</param>
    internal async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IReadOnlyCnasDbContext>();
            var now = _clock.UtcNow;
            var count = await db.PendingAdminActions.LongCountAsync(
                x => x.IsActive
                     && x.Status == PendingAdminActionStatus.Pending
                     && x.ExpiresAtUtc > now,
                ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastBacklog, count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown signal — surface up so ExecuteAsync's break-out runs.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "AdminActionBacklogObserver refresh failed; gauge will hold last known value {Backlog}.",
                Interlocked.Read(ref _lastBacklog));
        }
    }
}
