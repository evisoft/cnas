using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Interop.Batch;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R1710 / TOR INT 002 — Quartz job that picks the OLDEST <c>Queued</c>
/// offline-batch submission per fire and invokes
/// <see cref="IOfflineBatchProcessor.ProcessAsync"/>. Honors the peak-hour
/// gate (OffPeakOnly profile).
/// </summary>
/// <remarks>
/// <para>
/// <b>Peak-hour gate.</b> The job's profile is <c>OffPeakOnly</c>; the gate
/// short-circuits the fire when invoked during peak hours.
/// </para>
/// <para>
/// <b>Concurrency guard.</b> <see cref="DisallowConcurrentExecutionAttribute"/>
/// keeps two fires from racing the same submission. Picks the OLDEST
/// Queued submission per fire so the backlog drains in FIFO order.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class OfflineBatchProcessingJob : IJob
{
    /// <summary>Stable Quartz job identity.</summary>
    public const string JobIdentity = "offline-batch-processing";

    /// <summary>Stable Quartz trigger identity.</summary>
    public const string TriggerIdentity = "offline-batch-processing-trigger";

    /// <summary>Cron expression — every 5 minutes (peak-hour gate keeps it in the off-peak window).</summary>
    public const string Cron = "0 0/5 * * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate.</summary>
    public const string JobCode = JobScheduleProfileRegistry.OfflineBatchProcessing;

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<OfflineBatchProcessingJob> _logger;

    /// <summary>Constructs the job.</summary>
    /// <param name="scopes">DI scope factory.</param>
    /// <param name="peakHourGate">Peak-hour gate.</param>
    /// <param name="logger">Structured logger.</param>
    public OfflineBatchProcessingJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        ILogger<OfflineBatchProcessingJob> logger)
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
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();
        var processor = scope.ServiceProvider.GetRequiredService<IOfflineBatchProcessor>();

        var queued = await db.OfflineBatchSubmissions
            .Where(s => s.IsActive && s.Status == OfflineBatchStatus.Queued)
            .OrderBy(s => s.SubmittedAt)
            .ThenBy(s => s.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (queued is null)
        {
            _logger.LogInformation("OfflineBatchProcessingJob fired — no Queued submissions to process.");
            return;
        }

        var submissionSqid = sqids.Encode(queued.Id);
        var result = await processor.ProcessAsync(submissionSqid, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "OfflineBatchProcessingJob finalised submission {SubmissionId} (batchNumber={BatchNumber}).",
                queued.Id, queued.BatchNumber);
        }
        else
        {
            _logger.LogWarning(
                "OfflineBatchProcessingJob refused submission {SubmissionId}: {ErrorCode} {ErrorMessage}.",
                queued.Id, result.ErrorCode, result.ErrorMessage);
        }
    }
}
