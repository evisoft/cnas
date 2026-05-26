using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Financials;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Financials;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R0817 / TOR BP 1.2-H — Quartz job that scans every Active
/// <see cref="PenaltyRepaymentPlan"/> daily at 04:00 UTC and flips any plan
/// with an overdue installment (past due AND not paid for &gt;
/// <see cref="PenaltyRepaymentService.DefaultDetectionWindowDays"/> days) to
/// <see cref="PenaltyRepaymentPlanStatus.Defaulted"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency.</b> The default-detection flip is one-shot: once a plan
/// transitions to <see cref="PenaltyRepaymentPlanStatus.Defaulted"/> the
/// predicate on the next fire excludes it (Active filter). A retry mid-run
/// is therefore safe.
/// </para>
/// <para>
/// <see cref="DisallowConcurrentExecutionAttribute"/> is belt-and-braces —
/// the underlying flip is idempotent but parallel fires would only re-scan
/// the same Active set and waste DB round-trips.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class PenaltyRepaymentDefaultDetectionJob : IJob
{
    /// <summary>Stable Quartz job identity used for registration and lookups.</summary>
    public const string JobIdentity = "penalty-repayment-default-detection";

    /// <summary>Stable Quartz trigger identity paired with <see cref="JobIdentity"/>.</summary>
    public const string TriggerIdentity = "penalty-repayment-default-detection-trigger";

    /// <summary>Cron expression — daily at 04:00 UTC.</summary>
    public const string Cron = "0 0 4 * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate (OffPeakOnly profile).</summary>
    public const string JobCode = JobScheduleProfileRegistry.PenaltyRepaymentDefaultDetection;

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<PenaltyRepaymentDefaultDetectionJob> _logger;

    /// <summary>Constructs the job with its scope factory + logger dependencies.</summary>
    /// <param name="scopes">DI scope factory used to resolve scoped collaborators per fire.</param>
    /// <param name="peakHourGate">R2173 peak-hour gate consulted at the top of each fire.</param>
    /// <param name="logger">Structured logger.</param>
    public PenaltyRepaymentDefaultDetectionJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        ILogger<PenaltyRepaymentDefaultDetectionJob> logger)
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

        // R2173 / TOR PSR 004 — peak-hour gate. OffPeakOnly profile defers the
        // scan to the configured off-peak window. The cron is already 04:00 UTC
        // so the gate is belt-and-braces for emergency manual fires.
        if (await _peakHourGate.EvaluateAsync(JobCode, ct).ConfigureAwait(false) == PeakHourGateDecision.Skip)
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICnasDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<ICnasTimeProvider>();
        var service = scope.ServiceProvider.GetRequiredService<IPenaltyRepaymentService>();

        var todayUtc = DateOnly.FromDateTime(clock.UtcNow);
        var threshold = todayUtc.AddDays(-PenaltyRepaymentService.DefaultDetectionWindowDays);

        // Active plans whose earliest unpaid installment's DueDate is older
        // than the threshold. The query joins via the FK rather than relying
        // on a denormalised column; the per-fire volume is bounded by the
        // number of Active plans (small).
        var stalePlanIds = await db.PenaltyRepaymentPlans
            .Where(p => p.IsActive && p.Status == PenaltyRepaymentPlanStatus.Active
                && db.PenaltyRepaymentInstallments
                    .Any(i => i.IsActive
                        && i.PenaltyRepaymentPlanId == p.Id
                        && !i.IsPaid
                        && i.DueDate < threshold))
            .Select(p => p.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        if (stalePlanIds.Count == 0)
        {
            _logger.LogDebug("PenaltyRepaymentDefaultDetectionJob — no overdue plans to mark.");
            return;
        }

        int successCount = 0;
        int failureCount = 0;
        foreach (var id in stalePlanIds)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }
            try
            {
                var result = await service.MarkDefaultedAsync(id, ct).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    successCount++;
                }
                else
                {
                    failureCount++;
                    _logger.LogWarning(
                        "PenaltyRepaymentDefaultDetectionJob — MarkDefaultedAsync({PlanId}) returned {Code}: {Message}",
                        id, result.ErrorCode, result.ErrorMessage);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failureCount++;
                _logger.LogError(ex, "PenaltyRepaymentDefaultDetectionJob — MarkDefaultedAsync({PlanId}) threw.", id);
            }
        }

        _logger.LogInformation(
            "PenaltyRepaymentDefaultDetectionJob completed: scanned={Scanned} marked={Marked} failed={Failed}",
            stalePlanIds.Count, successCount, failureCount);
    }
}
