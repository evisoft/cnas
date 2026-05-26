using System;
using System.Linq;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Migration;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R2430 / R2431 / R2433 / TOR M4 — Quartz job that fires daily at
/// 02:15 UTC and triggers a DryRun import for one Active
/// <see cref="MigrationPlan"/> that has not been DryRun-imported in the
/// last 7 days. Honours the OffPeakOnly peak-hour gate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency guard.</b> <see cref="DisallowConcurrentExecutionAttribute"/>
/// keeps two fires from racing the same plan. The job picks the OLDEST
/// eligible plan per fire so the backlog rotates through every plan over
/// time.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class MigrationDryRunJob : IJob
{
    /// <summary>Stable Quartz job identity.</summary>
    public const string JobIdentity = "migration-dry-run";

    /// <summary>Stable Quartz trigger identity.</summary>
    public const string TriggerIdentity = "migration-dry-run-trigger";

    /// <summary>Cron expression — daily at 02:15 UTC.</summary>
    public const string Cron = "0 15 2 * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate.</summary>
    public const string JobCode = JobScheduleProfileRegistry.MigrationDryRun;

    /// <summary>Number of days between scheduled DryRun runs for the same plan.</summary>
    public const int RotationWindowDays = 7;

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<MigrationDryRunJob> _logger;

    /// <summary>Constructs the job.</summary>
    /// <param name="scopes">DI scope factory.</param>
    /// <param name="peakHourGate">Peak-hour gate.</param>
    /// <param name="logger">Structured logger.</param>
    public MigrationDryRunJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        ILogger<MigrationDryRunJob> logger)
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
        var importer = scope.ServiceProvider.GetRequiredService<IMigrationImporter>();

        var nowUtc = clock.UtcNow;
        var threshold = nowUtc.AddDays(-RotationWindowDays);

        // Find an Active plan whose most recent DryRun (Scheduled or DryRun trigger) is older than the rotation window
        // or that has no DryRun runs at all.
        var planQuery =
            from p in db.MigrationPlans
            where p.IsActive && p.Status == MigrationPlanStatus.Active
            let mostRecentDryRun = db.MigrationRuns
                .Where(r => r.PlanId == p.Id
                    && r.IsActive
                    && (r.TriggerKind == MigrationTriggerKind.Scheduled
                        || r.TriggerKind == MigrationTriggerKind.DryRun))
                .OrderByDescending(r => r.StartedAt)
                .Select(r => (DateTime?)r.StartedAt)
                .FirstOrDefault()
            where mostRecentDryRun == null || mostRecentDryRun < threshold
            orderby (mostRecentDryRun ?? DateTime.MinValue), p.Id
            select p;

        var picked = await planQuery.FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (picked is null)
        {
            _logger.LogInformation(
                "MigrationDryRunJob fired — no Active plans eligible for DryRun (rotation window={Days} days).",
                RotationWindowDays);
            return;
        }

        var planSqid = sqids.Encode(picked.Id);
        var result = await importer.ImportAsync(planSqid, MigrationTriggerKind.Scheduled, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "MigrationDryRunJob completed plan {PlanCode} run with status={Status}, rowsSeen={Rows}.",
                picked.PlanCode, result.Value.Status, result.Value.TotalSourceRowsSeen);
        }
        else
        {
            _logger.LogWarning(
                "MigrationDryRunJob refused plan {PlanCode}: {ErrorCode} {ErrorMessage}.",
                picked.PlanCode, result.ErrorCode, result.ErrorMessage);
        }
    }
}
