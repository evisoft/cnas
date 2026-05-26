using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Etl;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R0153 / TOR CF 19.05 — Quartz job that drives the daily contributor
/// period-projection rebuild. Fires at 03:00 UTC (one hour after the KPI
/// snapshot at 02:00) so reports running on the projection table see fresh
/// slice data without contending with the KPI calculator scans. Idempotent —
/// the underlying <see cref="IContributorPeriodProjectionService.RebuildAllAsync"/>
/// performs DELETE-then-INSERT per contributor.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DisallowConcurrentExecutionAttribute"/> is belt-and-braces — the
/// underlying service is idempotent, but parallel fires would only waste DB
/// round-trips for no benefit.
/// </para>
/// <para>
/// Emits <c>cnas.etl.contributor_projection_run{outcome=success|failure}</c>
/// per fire so operators chart success rate vs. failure-burst patterns on
/// the meter. The success increment lives inside the service; this job
/// emits the failure increment when the service throws so the failure
/// counter is not lost on exceptional paths.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class ContributorPeriodProjectionJob : IJob
{
    /// <summary>Stable Quartz job identity used for registration and lookups.</summary>
    public const string JobIdentity = "contributor-period-projection";

    /// <summary>Stable Quartz trigger identity paired with <see cref="JobIdentity"/>.</summary>
    public const string TriggerIdentity = "contributor-period-projection-trigger";

    /// <summary>Cron expression — daily at 03:00 UTC.</summary>
    public const string Cron = "0 0 3 * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate (OffPeakOnly profile).</summary>
    public const string JobCode = JobScheduleProfileRegistry.ContributorPeriodProjection;

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<ContributorPeriodProjectionJob> _logger;

    /// <summary>Constructs the projection job with its scope factory + logger dependencies.</summary>
    /// <param name="scopes">DI scope factory used to resolve scoped collaborators per fire.</param>
    /// <param name="peakHourGate">R2173 peak-hour gate consulted at the top of each fire.</param>
    /// <param name="logger">Structured logger.</param>
    public ContributorPeriodProjectionJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        ILogger<ContributorPeriodProjectionJob> logger)
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
        // projection rebuild to the configured off-peak window. The cron is
        // already 03:00 UTC so the gate is belt-and-braces for emergency manual fires.
        if (await _peakHourGate.EvaluateAsync(JobCode, ct).ConfigureAwait(false) == PeakHourGateDecision.Skip)
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IContributorPeriodProjectionService>();

        try
        {
            var result = await service.RebuildAllAsync(ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                _logger.LogInformation(
                    "ContributorPeriodProjectionJob completed: contributors={Count} slices={Slices} duration={DurationMs}ms",
                    result.Value.ContributorsProcessed,
                    result.Value.SlicesCreated,
                    result.Value.DurationMs);
            }
            else
            {
                CnasMeter.ContributorProjectionRun.Add(1,
                    new KeyValuePair<string, object?>("outcome", "failure"));
                _logger.LogWarning(
                    "ContributorPeriodProjectionJob returned failure {Code}: {Message}",
                    result.ErrorCode, result.ErrorMessage);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CnasMeter.ContributorProjectionRun.Add(1,
                new KeyValuePair<string, object?>("outcome", "failure"));
            _logger.LogError(ex, "ContributorPeriodProjectionJob threw.");
            throw;
        }
    }
}
