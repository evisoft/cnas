using Cnas.Ps.Application.ExternalSources;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R0203 / TOR CF 20.06 — Quartz job that triggers the RSP (Registrul de Stat
/// al Populației) ingestion run at 02:00 UTC each day. Honours the off-peak
/// peak-hour gate and defers to
/// <see cref="IExternalSourceIngestionService.TriggerScheduledRunAsync(string, DateOnly, System.Threading.CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Peak-hour gate.</b> The job's profile is <c>OffPeakOnly</c>; the gate
/// short-circuits the fire when invoked during peak hours. 02:00 UTC is
/// already inside the standard off-peak window so the gate is belt-and-braces.
/// </para>
/// <para>
/// <b>Placeholder connector.</b> The shipping
/// <c>RspExternalSourceConnector</c> returns a deterministic
/// <c>EXT_SRC.RSP_NOT_CONFIGURED</c> failure until the MEGA certificate and
/// MConnect agreement land. The job logs the failure and finalises the run
/// row regardless so operators have a per-run trail to verify cadence even
/// before the upstream connector is wired.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class RspIngestionJob : IJob
{
    /// <summary>Stable Quartz job identity.</summary>
    public const string JobIdentity = "rsp-ingestion";

    /// <summary>Stable Quartz trigger identity.</summary>
    public const string TriggerIdentity = "rsp-ingestion-trigger";

    /// <summary>Cron expression — daily at 02:00 UTC (inside the standard off-peak window).</summary>
    public const string Cron = "0 0 2 * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate.</summary>
    public const string JobCode = JobScheduleProfileRegistry.RspIngestion;

    /// <summary>Upper-case source-system code consumed by the ingestion service dispatch.</summary>
    public const string SourceCode = "RSP";

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<RspIngestionJob> _logger;

    /// <summary>Constructs the job.</summary>
    /// <param name="scopes">DI scope factory.</param>
    /// <param name="peakHourGate">Peak-hour gate.</param>
    /// <param name="logger">Structured logger.</param>
    public RspIngestionJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        ILogger<RspIngestionJob> logger)
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
        var clock = scope.ServiceProvider.GetRequiredService<ICnasTimeProvider>();
        var ingestion = scope.ServiceProvider.GetRequiredService<IExternalSourceIngestionService>();

        var asOfDate = clock.TodayUtc;
        var result = await ingestion.TriggerScheduledRunAsync(SourceCode, asOfDate, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "RspIngestionJob completed asOfDate={AsOfDate} runNumber={RunNumber} status={Status}.",
                asOfDate, result.Value.RunNumber, result.Value.Status);
        }
        else
        {
            _logger.LogWarning(
                "RspIngestionJob refused asOfDate={AsOfDate}: {ErrorCode} {ErrorMessage}.",
                asOfDate, result.ErrorCode, result.ErrorMessage);
        }
    }
}
