using Cnas.Ps.Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Observability;

/// <summary>
/// One-shot hosted service that registers every CNAS observable-gauge callback with
/// the static <see cref="CnasMeter"/>. Runs once at process startup so the gauges
/// are subscribed before the OTel exporter samples them. R0040 follow-up.
/// </summary>
/// <remarks>
/// <para>
/// Gauges have to be registered after the dependencies that back them are resolvable
/// — specifically the singleton <see cref="AuditWriteQueue"/> and the singleton
/// <see cref="AdminActionBacklogObserver"/>. Doing this from a hosted service
/// guarantees the DI container is fully built before we capture closures over the
/// dependencies.
/// </para>
/// <para>
/// <see cref="StartAsync"/> is the only meaningful path; <see cref="StopAsync"/> is
/// a no-op because instrument registration on a <see cref="System.Diagnostics.Metrics.Meter"/>
/// is process-static and cannot be undone short of disposing the meter (which we
/// intentionally do not, so process-end exports still flush cleanly).
/// </para>
/// </remarks>
public sealed class CnasMetricsInitializer : IHostedService
{
    private readonly AuditWriteQueue _queue;
    private readonly AdminActionBacklogObserver _backlog;
    private readonly IOptions<AuditArchiveOptions> _archiveOptions;

    /// <summary>Constructs the initializer with its singleton dependencies.</summary>
    /// <param name="queue">Singleton audit-write queue whose depth feeds <c>cnas.audit.queue.depth</c>.</param>
    /// <param name="backlog">Background observer whose cached count feeds <c>cnas.admin.action.backlog</c>.</param>
    /// <param name="archiveOptions">Audit-archive options snapshot — used to locate the spill directory for the archive-size gauge.</param>
    public CnasMetricsInitializer(
        AuditWriteQueue queue,
        AdminActionBacklogObserver backlog,
        IOptions<AuditArchiveOptions> archiveOptions)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(backlog);
        ArgumentNullException.ThrowIfNull(archiveOptions);
        _queue = queue;
        _backlog = backlog;
        _archiveOptions = archiveOptions;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        CnasMeter.RegisterAuditQueueDepthGauge(_queue);

        // The archive-size gauge counts the pending replay files under the configured
        // spill directory. Any I/O hiccup (missing mount, permissions error) is
        // swallowed and surfaced as 0 — the gauge represents pending replays, not
        // I/O health; a missing directory means "no pending replays".
        var archivePath = _archiveOptions.Value.LocalPath;
        CnasMeter.RegisterAuditArchiveSizeGauge(() =>
        {
            try
            {
                if (!Directory.Exists(archivePath))
                {
                    return 0L;
                }
                return Directory.GetFiles(archivePath, "audit-*.json").LongLength;
            }
            catch
            {
                return 0L;
            }
        });

        CnasMeter.RegisterAdminActionBacklogGauge(() => _backlog.LastBacklog);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
