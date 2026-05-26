using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.DataClassification;
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
/// R2279 / TOR SEC 033 — Quartz job that captures a fresh
/// <see cref="ClassificationCatalogSnapshot"/> on a weekly cadence and
/// automatically computes drift against the most-recent prior
/// <c>Captured</c> snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Peak-hour gate.</b> The job's profile is <c>OffPeakOnly</c>; the gate
/// emits a single <c>cnas.peak_hour.gate{decision=skip}</c> counter when the
/// fire happens during a peak hour and the job returns immediately.
/// </para>
/// <para>
/// <b>Concurrency guard.</b> <see cref="DisallowConcurrentExecutionAttribute"/>
/// keeps two fires from racing — the scanner is reflection-only and the
/// per-fire DB inserts are scoped to one snapshot row.
/// </para>
/// <para>
/// <b>Cadence.</b> Weekly Sunday at 03:30 UTC, well inside the documented
/// off-peak window. Reflection scans are cheap; the cadence is set to weekly
/// rather than nightly because the Contracts assembly changes infrequently
/// after each release.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class ClassificationCatalogSnapshotJob : IJob
{
    /// <summary>Stable Quartz job identity used for registration and lookups.</summary>
    public const string JobIdentity = "classification-catalog-snapshot";

    /// <summary>Stable Quartz trigger identity paired with <see cref="JobIdentity"/>.</summary>
    public const string TriggerIdentity = "classification-catalog-snapshot-trigger";

    /// <summary>Cron expression — weekly Sunday at 03:30 UTC.</summary>
    public const string Cron = "0 30 3 ? * SUN";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate (OffPeakOnly profile).</summary>
    public const string JobCode = JobScheduleProfileRegistry.ClassificationCatalogSnapshot;

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<ClassificationCatalogSnapshotJob> _logger;

    /// <summary>Constructs the job with its collaborators.</summary>
    /// <param name="scopes">Scope factory used to resolve scoped collaborators per fire.</param>
    /// <param name="peakHourGate">R2173 peak-hour gate consulted at the top of each fire.</param>
    /// <param name="logger">Structured logger.</param>
    public ClassificationCatalogSnapshotJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        ILogger<ClassificationCatalogSnapshotJob> logger)
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

        // R2173 / TOR PSR 004 — peak-hour gate. OffPeakOnly profile.
        if (await _peakHourGate.EvaluateAsync(JobCode, ct).ConfigureAwait(false) == PeakHourGateDecision.Skip)
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IClassificationCatalogService>();
        var db = scope.ServiceProvider.GetRequiredService<IReadOnlyCnasDbContext>();

        try
        {
            // Pick the previous Captured snapshot BEFORE inserting the new one so
            // the new row cannot self-match as a baseline.
            var previousId = await db.ClassificationCatalogSnapshots
                .Where(s => s.Status == ClassificationSnapshotStatus.Captured && s.IsActive)
                .OrderByDescending(s => s.CapturedAt)
                .ThenByDescending(s => s.Id)
                .Select(s => (long?)s.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            var captured = await service.CaptureScheduledSnapshotAsync(ct).ConfigureAwait(false);
            if (captured.IsFailure)
            {
                _logger.LogError(
                    "ClassificationCatalogSnapshotJob capture failed (code={Code}, message={Message}).",
                    captured.ErrorCode, captured.ErrorMessage);
                await EmitJobFailureAuditAsync(scope.ServiceProvider, captured.ErrorMessage ?? "(no message)", ct)
                    .ConfigureAwait(false);
                return;
            }

            _logger.LogInformation(
                "ClassificationCatalogSnapshotJob captured snapshot {SnapshotSqid} (types={Types}, classified={Classified}, unclassified={Unclassified}).",
                captured.Value.Id,
                captured.Value.TotalTypesScanned,
                captured.Value.TotalPropertiesClassified,
                captured.Value.TotalPropertiesUnclassified);

            if (previousId is { } baselineId)
            {
                // Decode-then-re-encode the captured Sqid is overkill — pass the Sqid
                // for the freshly captured row directly. Resolve the baseline Sqid
                // via the SqidService surface.
                var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();
                var baselineSqid = sqids.Encode(baselineId);
                var driftResult = await service.ComputeDriftAsync(baselineSqid, captured.Value.Id, ct)
                    .ConfigureAwait(false);
                if (driftResult.IsSuccess)
                {
                    _logger.LogInformation(
                        "ClassificationCatalogSnapshotJob drift comparison persisted {Count} findings (baseline={Baseline}, current={Current}).",
                        driftResult.Value.FindingsCount,
                        baselineSqid,
                        captured.Value.Id);
                }
                else
                {
                    _logger.LogWarning(
                        "ClassificationCatalogSnapshotJob drift comparison failed (code={Code}, message={Message}).",
                        driftResult.ErrorCode, driftResult.ErrorMessage);
                }
            }
            else
            {
                _logger.LogInformation(
                    "ClassificationCatalogSnapshotJob skipped drift comparison — no prior snapshot exists.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await EmitJobFailureAuditAsync(scope.ServiceProvider, ex.GetType().Name + ": " + ex.Message, ct)
                .ConfigureAwait(false);
            _logger.LogError(ex, "ClassificationCatalogSnapshotJob crashed.");
        }
    }

    /// <summary>
    /// Writes the Critical <c>CLASSIFICATION.JOB_FAILED</c> audit row when the
    /// snapshot pipeline crashed before the service could finalise the row.
    /// Defensive — never throws back to Quartz.
    /// </summary>
    /// <param name="sp">DI scope's service provider.</param>
    /// <param name="reason">Human-readable reason persisted to the audit details.</param>
    /// <param name="ct">Cancellation propagated from the fire.</param>
    private static async Task EmitJobFailureAuditAsync(IServiceProvider sp, string reason, CancellationToken ct)
    {
        try
        {
            var audit = sp.GetRequiredService<IAuditService>();
            var details = JsonSerializer.Serialize(new { reason });
            await audit.RecordAsync(
                "CLASSIFICATION.JOB_FAILED",
                AuditSeverity.Critical,
                actorId: "system",
                targetEntity: nameof(ClassificationCatalogSnapshot),
                targetEntityId: null,
                detailsJson: details,
                sourceIp: null,
                correlationId: null,
                ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Defence-in-depth — never throw out to Quartz from an audit emit.
        }
    }
}
