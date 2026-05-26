using System;
using System.Linq;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Recalculation;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R1503 / TOR §3.7-D — Quartz job that picks up <c>LegalChangeEvent</c> rows
/// in <see cref="LegalChangeEventStatus.Ready"/> whose <c>EffectiveFrom</c>
/// has elapsed and starts a DryRun via
/// <see cref="IMassRecalculationService.StartDryRunAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Peak-hour gate.</b> The job's profile is <c>OffPeakOnly</c>; the gate
/// short-circuits the fire when invoked outside the off-peak window.
/// </para>
/// <para>
/// <b>Concurrency guard.</b> <see cref="DisallowConcurrentExecutionAttribute"/>
/// keeps two fires from racing the same Ready event. Picks the OLDEST one
/// Ready event per fire — the operator can fan out by raising more events
/// while the previous one is still being recalculated.
/// </para>
/// <para>
/// <b>Idempotency.</b> A successful DryRun flips the event status to
/// <see cref="LegalChangeEventStatus.ReviewPending"/>, so a subsequent fire
/// will skip the row. A failed DryRun leaves the event in
/// <see cref="LegalChangeEventStatus.Recalculating"/> — operators must
/// inspect the failure reason before re-driving the row.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class MassRecalculationApplyJob : IJob
{
    /// <summary>Stable Quartz job identity used for registration and lookups.</summary>
    public const string JobIdentity = "mass-recalculation-apply";

    /// <summary>Stable Quartz trigger identity paired with <see cref="JobIdentity"/>.</summary>
    public const string TriggerIdentity = "mass-recalculation-apply-trigger";

    /// <summary>Cron expression — daily at 02:30 UTC, inside the off-peak window.</summary>
    public const string Cron = "0 30 2 * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate (OffPeakOnly profile).</summary>
    public const string JobCode = JobScheduleProfileRegistry.MassRecalculationApply;

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<MassRecalculationApplyJob> _logger;

    /// <summary>Constructs the job.</summary>
    /// <param name="scopes">Scope factory used to resolve scoped collaborators per fire.</param>
    /// <param name="peakHourGate">R2173 peak-hour gate.</param>
    /// <param name="logger">Structured logger.</param>
    public MassRecalculationApplyJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        ILogger<MassRecalculationApplyJob> logger)
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

        // R2173 — gate first.
        if (await _peakHourGate.EvaluateAsync(JobCode, ct).ConfigureAwait(false) == PeakHourGateDecision.Skip)
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICnasDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<ICnasTimeProvider>();
        var service = scope.ServiceProvider.GetRequiredService<IMassRecalculationService>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        var today = clock.TodayUtc;
        // Pick the OLDEST Ready event whose effective-from date is in the past
        // (or today). The next fire picks up the next-oldest until the backlog
        // drains.
        var evt = await db.LegalChangeEvents
            .Where(e => e.IsActive
                && e.Status == LegalChangeEventStatus.Ready
                && e.EffectiveFrom <= today)
            .OrderBy(e => e.EffectiveFrom)
            .ThenBy(e => e.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (evt is null)
        {
            _logger.LogInformation("MassRecalculationApplyJob fired — no Ready events to process.");
            return;
        }

        var legalChangeSqid = sqids.Encode(evt.Id);
        var result = await service.StartDryRunAsync(legalChangeSqid, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "MassRecalculationApplyJob started DryRun for legalChangeId={LegalChangeId} runId={RunId}.",
                evt.Id, result.Value.Id);
        }
        else
        {
            _logger.LogWarning(
                "MassRecalculationApplyJob refused to start DryRun for legalChangeId={LegalChangeId}: {ErrorCode} {ErrorMessage}.",
                evt.Id, result.ErrorCode, result.ErrorMessage);
        }
    }
}
