using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.SensitiveActions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R2273 / TOR SEC 027 — Quartz job that sweeps stale <c>SensitiveAdminAction</c> rows
/// (status <c>PendingApproval</c>, <c>ExpiresAt &lt; now</c>) and flips them to
/// <c>Expired</c>. Cadence: every 15 minutes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Peak-hour gate.</b> The job's profile is <c>Always</c> — small, cheap, must run
/// regardless of business hours so the operator dashboards don't accumulate stale
/// rows during peak.
/// </para>
/// <para>
/// <b>Concurrency guard.</b> <see cref="DisallowConcurrentExecutionAttribute"/> keeps
/// two fires from racing the same data set. The service-side flip itself is idempotent
/// (the predicate excludes already-flipped rows) — the guard is belt-and-braces.
/// </para>
/// <para>
/// <b>Failure handling.</b> Unhandled exceptions inside the service propagate up to
/// Quartz which routes them through the <c>FailedJobListener</c> DLQ. The service
/// already emits a Critical audit on each successful sweep — no need for the job to
/// duplicate it.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class SensitiveAdminActionExpirySweepJob : IJob
{
    /// <summary>Stable Quartz job identity used for registration and lookups.</summary>
    public const string JobIdentity = "sensitive-admin-action-expiry-sweep";

    /// <summary>Stable Quartz trigger identity paired with <see cref="JobIdentity"/>.</summary>
    public const string TriggerIdentity = "sensitive-admin-action-expiry-sweep-trigger";

    /// <summary>Cron expression — every 15 minutes (00, 15, 30, 45).</summary>
    public const string Cron = "0 0/15 * * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate (Always profile).</summary>
    public const string JobCode = JobScheduleProfileRegistry.SensitiveAdminActionExpirySweep;

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<SensitiveAdminActionExpirySweepJob> _logger;

    /// <summary>Constructs the job with its collaborators.</summary>
    /// <param name="scopes">Scope factory used to resolve scoped collaborators per fire.</param>
    /// <param name="peakHourGate">R2173 peak-hour gate consulted at the top of each fire.</param>
    /// <param name="logger">Structured logger.</param>
    public SensitiveAdminActionExpirySweepJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        ILogger<SensitiveAdminActionExpirySweepJob> logger)
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

        // R2173 — peak-hour gate. Always profile so the gate is effectively a no-op,
        // but the call is retained so a future operator override (HardOverride flag)
        // can suppress the sweep without changing the job.
        if (await _peakHourGate.EvaluateAsync(JobCode, ct).ConfigureAwait(false) == PeakHourGateDecision.Skip)
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISensitiveAdminActionService>();

        var result = await service.SweepExpiredAsync(ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            _logger.LogWarning(
                "SensitiveAdminActionExpirySweepJob failed: {Code} {Message}",
                result.ErrorCode, result.ErrorMessage);
            return;
        }

        if (result.Value > 0)
        {
            _logger.LogInformation(
                "SensitiveAdminActionExpirySweepJob flipped {Count} rows to Expired.",
                result.Value);
        }
    }
}
