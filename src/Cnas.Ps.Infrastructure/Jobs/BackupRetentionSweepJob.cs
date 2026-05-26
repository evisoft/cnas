using System;
using System.Threading.Tasks;
using Cnas.Ps.Application.Backups;
using Cnas.Ps.Application.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R2307 / TOR SEC 060 — Quartz job that fires daily at 03:30 UTC and
/// invokes <c>IBackupOrchestrator.SweepExpiredRunsAsync</c> to purge
/// payloads past their retention window. Honours the OffPeakOnly peak-hour
/// gate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency guard.</b> <see cref="DisallowConcurrentExecutionAttribute"/>
/// keeps two fires from racing the same set of expired runs. The sweep
/// itself is idempotent — a re-fire after a partial sweep finishes the
/// remaining work without double-deleting.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class BackupRetentionSweepJob : IJob
{
    /// <summary>Stable Quartz job identity.</summary>
    public const string JobIdentity = "backup-retention-sweep";

    /// <summary>Stable Quartz trigger identity.</summary>
    public const string TriggerIdentity = "backup-retention-sweep-trigger";

    /// <summary>Cron expression — daily at 03:30 UTC.</summary>
    public const string Cron = "0 30 3 * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate.</summary>
    public const string JobCode = JobScheduleProfileRegistry.BackupRetentionSweep;

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<BackupRetentionSweepJob> _logger;

    /// <summary>Constructs the job.</summary>
    /// <param name="scopes">DI scope factory.</param>
    /// <param name="peakHourGate">Peak-hour gate.</param>
    /// <param name="logger">Structured logger.</param>
    public BackupRetentionSweepJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        ILogger<BackupRetentionSweepJob> logger)
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

        if (await _peakHourGate.EvaluateAsync(JobCode, ct).ConfigureAwait(false) == PeakHourGateDecision.Skip)
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IBackupOrchestrator>();

        var result = await orchestrator.SweepExpiredRunsAsync(ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "BackupRetentionSweepJob purged {Count} expired backup-run payload(s).",
                result.Value);
        }
        else
        {
            _logger.LogWarning(
                "BackupRetentionSweepJob failed: {ErrorCode} {ErrorMessage}.",
                result.ErrorCode, result.ErrorMessage);
        }
    }
}
