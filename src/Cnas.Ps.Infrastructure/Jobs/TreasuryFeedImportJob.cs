using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.Treasury.Feed;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R1810 / TOR BP 1.2-I — Quartz job that ingests the daily Treasury feed
/// (BASS receipts) at 04:00 UTC. Honours the peak-hour gate, computes the
/// target date (yesterday), and defers to
/// <see cref="ITreasuryFeedImporter.ImportAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Peak-hour gate.</b> The job's profile is <c>OffPeakOnly</c>; the gate
/// short-circuits the fire when invoked during peak hours. 04:00 UTC is
/// already inside the standard off-peak window so the gate is belt-and-braces.
/// </para>
/// <para>
/// <b>Concurrency guard.</b> <see cref="DisallowConcurrentExecutionAttribute"/>
/// keeps two fires from racing the same date. Idempotent at the registry
/// layer too: when a previously Completed import for the target date already
/// exists the job skips without inserting a new row.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class TreasuryFeedImportJob : IJob
{
    /// <summary>Stable Quartz job identity.</summary>
    public const string JobIdentity = "treasury-feed-import";

    /// <summary>Stable Quartz trigger identity.</summary>
    public const string TriggerIdentity = "treasury-feed-import-trigger";

    /// <summary>Cron expression — daily at 04:00 UTC (inside the standard off-peak window).</summary>
    public const string Cron = "0 0 4 * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate.</summary>
    public const string JobCode = JobScheduleProfileRegistry.TreasuryFeedImport;

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<TreasuryFeedImportJob> _logger;

    /// <summary>Constructs the job.</summary>
    /// <param name="scopes">DI scope factory.</param>
    /// <param name="peakHourGate">Peak-hour gate.</param>
    /// <param name="logger">Structured logger.</param>
    public TreasuryFeedImportJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        ILogger<TreasuryFeedImportJob> logger)
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
        var importer = scope.ServiceProvider.GetRequiredService<ITreasuryFeedImporter>();

        // Target date: yesterday in UTC. The Moldova local-time variant is
        // out of scope for this iteration — the existing clock abstraction
        // only exposes UtcNow / TodayUtc, and 04:00 UTC ≈ 06:00 EET which
        // is well after midnight Moldova-side.
        var targetDate = clock.TodayUtc.AddDays(-1);

        // Idempotency at the registry layer: skip when a Completed row exists.
        var alreadyDone = await db.TreasuryFeedImports
            .AnyAsync(
                i => i.FeedDate == targetDate
                    && i.Status == TreasuryFeedImportStatus.Completed
                    && i.IsActive,
                ct)
            .ConfigureAwait(false);
        if (alreadyDone)
        {
            _logger.LogInformation(
                "TreasuryFeedImportJob fired — feedDate={FeedDate} already has a Completed import; skipping.",
                targetDate);
            return;
        }

        var result = await importer.ImportAsync(targetDate, TreasuryFeedTriggerKind.Scheduled, ct)
            .ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "TreasuryFeedImportJob completed feedDate={FeedDate} with status={Status} rows={Total}.",
                targetDate, result.Value.Status, result.Value.RowsTotal);
        }
        else
        {
            _logger.LogWarning(
                "TreasuryFeedImportJob refused feedDate={FeedDate}: {ErrorCode} {ErrorMessage}.",
                targetDate, result.ErrorCode, result.ErrorMessage);
        }
    }
}
