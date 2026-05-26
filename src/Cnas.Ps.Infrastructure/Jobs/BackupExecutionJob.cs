using System;
using System.Linq;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Backups;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R2307 / TOR SEC 060 — Quartz job that fires every 30 minutes and triggers
/// a backup run for every Active <see cref="BackupPolicy"/> whose individual
/// cron expression fired within the last 30-minute window and that has not
/// already produced a successful run within that window. Honours the
/// OffPeakOnly peak-hour gate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency guard.</b> <see cref="DisallowConcurrentExecutionAttribute"/>
/// keeps two fires from racing the same set of policies. The per-policy
/// "already-fired-in-window" predicate makes the job idempotent — a
/// re-fire within the window is a no-op even without the guard.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class BackupExecutionJob : IJob
{
    /// <summary>Stable Quartz job identity.</summary>
    public const string JobIdentity = "backup-execution";

    /// <summary>Stable Quartz trigger identity.</summary>
    public const string TriggerIdentity = "backup-execution-trigger";

    /// <summary>Cron expression — every 30 minutes, on the half-hour boundary.</summary>
    public const string Cron = "0 0/30 * * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate.</summary>
    public const string JobCode = JobScheduleProfileRegistry.BackupExecution;

    /// <summary>Length of the look-back window the predicate uses to decide whether a policy's cron just fired.</summary>
    public static readonly TimeSpan LookBackWindow = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<BackupExecutionJob> _logger;

    /// <summary>Constructs the job.</summary>
    /// <param name="scopes">DI scope factory.</param>
    /// <param name="peakHourGate">Peak-hour gate.</param>
    /// <param name="logger">Structured logger.</param>
    public BackupExecutionJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        ILogger<BackupExecutionJob> logger)
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
        var db = scope.ServiceProvider.GetRequiredService<ICnasDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<ICnasTimeProvider>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IBackupOrchestrator>();

        var nowUtc = clock.UtcNow;
        var windowStart = nowUtc - LookBackWindow;

        // Pull every Active policy. Cron evaluation has to happen in-process because Quartz's
        // CronExpression isn't representable as an EF Core query.
        var activePolicies = await db.BackupPolicies
            .Where(p => p.IsActive && !p.IsArchived)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var policy in activePolicies)
        {
            ct.ThrowIfCancellationRequested();

            CronExpression cron;
            try
            {
                cron = new CronExpression(policy.CronSchedule);
            }
            catch (FormatException ex)
            {
                _logger.LogWarning(
                    ex,
                    "BackupExecutionJob skipped policy {PolicyCode}: cron expression '{Cron}' is malformed.",
                    policy.PolicyCode, policy.CronSchedule);
                continue;
            }

            // Did the cron fire within the look-back window?
            var lastFire = cron.GetTimeAfter(windowStart);
            if (lastFire is null || lastFire.Value.UtcDateTime > nowUtc)
            {
                continue;
            }

            // Idempotency — skip if any run for this policy has StartedAt inside the same look-back window.
            var alreadyRan = await db.BackupRuns
                .AnyAsync(r => r.PolicyId == policy.Id && r.StartedAt >= windowStart, ct)
                .ConfigureAwait(false);
            if (alreadyRan)
            {
                continue;
            }

            var policySqid = sqids.Encode(policy.Id);
            var result = await orchestrator.RunPolicyAsync(policySqid, BackupTriggerKind.Scheduled, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                _logger.LogInformation(
                    "BackupExecutionJob completed run {RunNumber} for policy {PolicyCode} status={Status}.",
                    result.Value.RunNumber, policy.PolicyCode, result.Value.Status);
            }
            else
            {
                _logger.LogWarning(
                    "BackupExecutionJob refused policy {PolicyCode}: {ErrorCode} {ErrorMessage}.",
                    policy.PolicyCode, result.ErrorCode, result.ErrorMessage);
            }
        }
    }
}
