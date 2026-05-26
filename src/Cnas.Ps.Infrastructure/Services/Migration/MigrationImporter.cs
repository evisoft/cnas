using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Migration;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.Migration;

/// <summary>
/// R2430 / R2431 / R2433 / TOR M4 — production implementation of
/// <see cref="IMigrationImporter"/>. Drives a single
/// <see cref="MigrationRun"/> through its lifecycle, streaming source
/// records, mapping them via the resolved
/// <see cref="IMigrationRecordMapper"/>, persisting per-batch counters and
/// staging rows, and finalising with reconciliation.
/// </summary>
/// <remarks>
/// <para>
/// <b>DryRun semantics.</b> When the supplied <c>trigger</c> is
/// <see cref="MigrationTriggerKind.DryRun"/> or
/// <see cref="MigrationTriggerKind.Scheduled"/>, the importer persists
/// staging rows with <c>IsCommitted=false</c> and never flips the flag.
/// Apply mode (<see cref="MigrationTriggerKind.Manual"/>) flips
/// <c>IsCommitted=true</c> once the mapper validation succeeds.
/// </para>
/// </remarks>
public sealed class MigrationImporter : IMigrationImporter
{
    /// <summary>Cached JSON serializer options shared across audit + reconciliation payloads.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IEnumerable<IMigrationSource> _sources;
    private readonly IEnumerable<IMigrationRecordMapper> _mappers;
    private readonly IPeakHourGate _peakHourGate;
    private readonly IMigrationReconciler _reconciler;
    private readonly ILogger<MigrationImporter> _logger;

    /// <summary>Constructs the importer with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="sources">Registered source adapters; resolved by SourceKind.</param>
    /// <param name="mappers">Registered mappers; resolved by TargetEntityName.</param>
    /// <param name="peakHourGate">Peak-hour gate consulted on every manual / scheduled fire.</param>
    /// <param name="reconciler">Reconciler invoked at the end of every successful run.</param>
    /// <param name="logger">Structured logger.</param>
    public MigrationImporter(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IEnumerable<IMigrationSource> sources,
        IEnumerable<IMigrationRecordMapper> mappers,
        IPeakHourGate peakHourGate,
        IMigrationReconciler reconciler,
        ILogger<MigrationImporter> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(mappers);
        ArgumentNullException.ThrowIfNull(peakHourGate);
        ArgumentNullException.ThrowIfNull(reconciler);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _sources = sources;
        _mappers = mappers;
        _peakHourGate = peakHourGate;
        _reconciler = reconciler;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<MigrationRunSummaryDto>> ImportAsync(
        string planSqid,
        MigrationTriggerKind trigger,
        CancellationToken cancellationToken = default)
    {
        // Step 1: decode + load the plan.
        var decoded = _sqids.TryDecode(planSqid);
        if (decoded.IsFailure)
        {
            return Result<MigrationRunSummaryDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var plan = await _db.MigrationPlans
            .FirstOrDefaultAsync(p => p.Id == decoded.Value && p.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (plan is null)
        {
            return Result<MigrationRunSummaryDto>.Failure(
                IMigrationImporter.PlanNotFoundCode,
                "Migration plan not found.");
        }
        if (plan.Status != MigrationPlanStatus.Active)
        {
            return Result<MigrationRunSummaryDto>.Failure(
                IMigrationImporter.PlanNotActiveCode,
                $"Migration plan must be Active to run; current status is {plan.Status}.");
        }

        // Step 2: peak-hour gate (manual triggers respect the same gate as scheduled).
        if (trigger == MigrationTriggerKind.Manual || trigger == MigrationTriggerKind.DryRun)
        {
            if (await _peakHourGate.EvaluateAsync("MigrationManual", cancellationToken).ConfigureAwait(false)
                == PeakHourGateDecision.Skip)
            {
                return Result<MigrationRunSummaryDto>.Failure(
                    IMigrationImporter.PeakHourGateBlockedCode,
                    "Manual migration imports are gated outside the off-peak window.");
            }
        }

        // Step 3: resolve source + mapper.
        var source = _sources.FirstOrDefault(s => s.SourceKind == plan.SourceKind);
        if (source is null)
        {
            return Result<MigrationRunSummaryDto>.Failure(
                IMigrationImporter.SourceNotConfiguredCode,
                $"No migration source registered for SourceKind={plan.SourceKind}.");
        }
        var mapper = ResolveMapper(plan.TargetEntityName);

        // Step 4: create the run row.
        var startedAt = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "system";
        var isDryRun = trigger != MigrationTriggerKind.Manual;
        var run = new MigrationRun
        {
            PlanId = plan.Id,
            TriggerKind = trigger,
            Status = MigrationRunStatus.Pending,
            StartedAt = startedAt,
            IsDryRun = isDryRun,
            CreatedAtUtc = startedAt,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.MigrationRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.MigrationRunStarted.Add(
            1,
            new KeyValuePair<string, object?>("trigger_kind", trigger.ToString()),
            new KeyValuePair<string, object?>("target_entity", plan.TargetEntityName));

        // Step 5: stream source, batch + map.
        run.Status = MigrationRunStatus.Running;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var batchOrdinal = 0;
        var sawCriticalFailure = false;
        var batchBuffer = new List<MigrationSourceRecord>(plan.BatchSize);

        try
        {
            await foreach (var record in source.StreamAsync(plan, cancellationToken).ConfigureAwait(false))
            {
                run.TotalSourceRowsSeen++;
                batchBuffer.Add(record);
                if (batchBuffer.Count >= plan.BatchSize)
                {
                    batchOrdinal++;
                    await ProcessBatchAsync(plan, run, mapper, batchBuffer, batchOrdinal, actor, isDryRun, cancellationToken)
                        .ConfigureAwait(false);
                    batchBuffer.Clear();
                }
            }
            if (batchBuffer.Count > 0)
            {
                batchOrdinal++;
                await ProcessBatchAsync(plan, run, mapper, batchBuffer, batchOrdinal, actor, isDryRun, cancellationToken)
                    .ConfigureAwait(false);
                batchBuffer.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            run.Status = MigrationRunStatus.Cancelled;
            run.CompletedAt = _clock.UtcNow;
            run.FailureReason = "Run cancelled before completion.";
            await _db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            await EmitAuditAsync(
                IMigrationImporter.AuditRunFailed,
                AuditSeverity.Critical,
                actor,
                run.Id,
                new { runSqid = _sqids.Encode(run.Id), planSqid = _sqids.Encode(plan.Id), reason = "cancelled" },
                CancellationToken.None).ConfigureAwait(false);
            CnasMeter.MigrationRunCompleted.Add(
                1,
                new KeyValuePair<string, object?>("target_entity", plan.TargetEntityName),
                new KeyValuePair<string, object?>("terminal_status", run.Status.ToString()));
            return Result<MigrationRunSummaryDto>.Success(ToSummary(run, plan));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Status = MigrationRunStatus.Failed;
            run.CompletedAt = _clock.UtcNow;
            run.FailureReason = SanitiseFailureMessage(ex.Message);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError(ex, "MigrationImporter run {RunId} for plan {PlanCode} failed.", run.Id, plan.PlanCode);
            await EmitAuditAsync(
                IMigrationImporter.AuditRunFailed,
                AuditSeverity.Critical,
                actor,
                run.Id,
                new { runSqid = _sqids.Encode(run.Id), planSqid = _sqids.Encode(plan.Id) },
                cancellationToken).ConfigureAwait(false);
            CnasMeter.MigrationRunCompleted.Add(
                1,
                new KeyValuePair<string, object?>("target_entity", plan.TargetEntityName),
                new KeyValuePair<string, object?>("terminal_status", run.Status.ToString()));
            return Result<MigrationRunSummaryDto>.Success(ToSummary(run, plan));
        }

        sawCriticalFailure = run.TotalRowsFailed > 0;

        // Step 6: finalise run.
        run.Status = sawCriticalFailure
            ? MigrationRunStatus.CompletedWithErrors
            : MigrationRunStatus.Completed;
        run.CompletedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            IMigrationImporter.AuditRunCompleted,
            AuditSeverity.Information,
            actor,
            run.Id,
            new
            {
                runSqid = _sqids.Encode(run.Id),
                planSqid = _sqids.Encode(plan.Id),
                status = run.Status.ToString(),
                run.TotalSourceRowsSeen,
                run.TotalRowsImported,
                run.TotalRowsUpdated,
                run.TotalRowsSkipped,
                run.TotalRowsFailed,
                run.IsDryRun,
            },
            cancellationToken).ConfigureAwait(false);

        CnasMeter.MigrationRunCompleted.Add(
            1,
            new KeyValuePair<string, object?>("target_entity", plan.TargetEntityName),
            new KeyValuePair<string, object?>("terminal_status", run.Status.ToString()));

        // Step 7: reconcile. Best-effort — we never fail the run because the
        // reconciler crashed; the per-reconciler audit will surface the issue.
        var runSqid = _sqids.Encode(run.Id);
        var reconciliation = await _reconciler.ReconcileAsync(runSqid, cancellationToken).ConfigureAwait(false);
        if (reconciliation.IsFailure)
        {
            _logger.LogWarning(
                "MigrationImporter post-run reconciliation for run {RunId} failed: {ErrorCode} {ErrorMessage}.",
                run.Id, reconciliation.ErrorCode, reconciliation.ErrorMessage);
        }

        return Result<MigrationRunSummaryDto>.Success(ToSummary(run, plan));
    }

    /// <summary>
    /// Maps every record in <paramref name="batch"/>, persists the resulting
    /// <see cref="MigrationStagingRow"/> entries, and flushes a
    /// <see cref="MigrationBatch"/> counter row.
    /// </summary>
    /// <param name="plan">Active plan.</param>
    /// <param name="run">Active run.</param>
    /// <param name="mapper">Resolved mapper.</param>
    /// <param name="batch">Source records to process.</param>
    /// <param name="batchOrdinal">1-based batch ordinal.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="isDryRun">When true the staging rows remain uncommitted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task ProcessBatchAsync(
        MigrationPlan plan,
        MigrationRun run,
        IMigrationRecordMapper mapper,
        IReadOnlyList<MigrationSourceRecord> batch,
        int batchOrdinal,
        string actor,
        bool isDryRun,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var now = _clock.UtcNow;
        var batchCounters = new BatchCounters();

        for (var i = 0; i < batch.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = batch[i];
            var mapped = await mapper.MapAsync(record, plan, cancellationToken).ConfigureAwait(false);
            if (mapped.IsFailure)
            {
                batchCounters.Failed++;
                run.TotalRowsFailed++;
                _db.MigrationFindings.Add(new MigrationFinding
                {
                    RunId = run.Id,
                    BatchOrdinal = batchOrdinal,
                    RowOrdinalInBatch = i,
                    Severity = MigrationFindingSeverity.Critical,
                    FindingCode = mapped.ErrorCode ?? "MAPPING.FAILED",
                    Description = mapped.ErrorMessage ?? "Mapping failed without a description.",
                    SourceFingerprint = record.SourceFingerprint,
                    Acknowledged = false,
                    CreatedAtUtc = now,
                    CreatedBy = actor,
                    IsActive = true,
                });
                CnasMeter.MigrationRowProcessed.Add(
                    1,
                    new KeyValuePair<string, object?>("target_entity", plan.TargetEntityName),
                    new KeyValuePair<string, object?>("outcome", "failed"));
                continue;
            }

            // Persist findings (info / warning) from the mapper.
            foreach (var finding in mapped.Value.Findings)
            {
                _db.MigrationFindings.Add(new MigrationFinding
                {
                    RunId = run.Id,
                    BatchOrdinal = batchOrdinal,
                    RowOrdinalInBatch = i,
                    Severity = finding.Severity,
                    FindingCode = finding.FindingCode,
                    Description = finding.Description,
                    SourceFingerprint = record.SourceFingerprint,
                    Acknowledged = false,
                    CreatedAtUtc = now,
                    CreatedBy = actor,
                    IsActive = true,
                });
            }

            // Look for existing staging row with same TargetEntityKey + run.
            var existing = await _db.MigrationStagingRows
                .FirstOrDefaultAsync(
                    r => r.RunId == run.Id
                        && r.TargetEntityKey == mapped.Value.TargetEntityKey
                        && r.IsActive,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                _db.MigrationStagingRows.Add(new MigrationStagingRow
                {
                    RunId = run.Id,
                    BatchOrdinal = batchOrdinal,
                    RowOrdinalInBatch = i,
                    TargetEntityName = plan.TargetEntityName,
                    TargetEntityKey = mapped.Value.TargetEntityKey,
                    MappedFieldsJson = mapped.Value.FieldsJson,
                    SourceFingerprint = record.SourceFingerprint,
                    IsCommitted = !isDryRun,
                    CommittedAt = isDryRun ? null : now,
                    CreatedAtUtc = now,
                    CreatedBy = actor,
                    IsActive = true,
                });
                batchCounters.Imported++;
                run.TotalRowsImported++;
                CnasMeter.MigrationRowProcessed.Add(
                    1,
                    new KeyValuePair<string, object?>("target_entity", plan.TargetEntityName),
                    new KeyValuePair<string, object?>("outcome", "imported"));
            }
            else
            {
                // Idempotent update path: same fingerprint = skip; otherwise update.
                if (existing.SourceFingerprint == record.SourceFingerprint
                    && existing.MappedFieldsJson == mapped.Value.FieldsJson)
                {
                    batchCounters.Skipped++;
                    run.TotalRowsSkipped++;
                    CnasMeter.MigrationRowProcessed.Add(
                        1,
                        new KeyValuePair<string, object?>("target_entity", plan.TargetEntityName),
                        new KeyValuePair<string, object?>("outcome", "skipped"));
                }
                else
                {
                    existing.MappedFieldsJson = mapped.Value.FieldsJson;
                    existing.SourceFingerprint = record.SourceFingerprint;
                    existing.IsCommitted = !isDryRun;
                    existing.CommittedAt = isDryRun ? null : now;
                    existing.UpdatedAtUtc = now;
                    existing.UpdatedBy = actor;
                    batchCounters.Updated++;
                    run.TotalRowsUpdated++;
                    CnasMeter.MigrationRowProcessed.Add(
                        1,
                        new KeyValuePair<string, object?>("target_entity", plan.TargetEntityName),
                        new KeyValuePair<string, object?>("outcome", "updated"));
                }
            }
        }

        sw.Stop();

        _db.MigrationBatches.Add(new MigrationBatch
        {
            RunId = run.Id,
            BatchOrdinal = batchOrdinal,
            RowsInBatch = batch.Count,
            RowsImported = batchCounters.Imported,
            RowsUpdated = batchCounters.Updated,
            RowsSkipped = batchCounters.Skipped,
            RowsFailed = batchCounters.Failed,
            DurationMs = sw.ElapsedMilliseconds,
            ProcessedAt = _clock.UtcNow,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the mapper whose <see cref="IMigrationRecordMapper.TargetEntityName"/>
    /// matches <paramref name="targetEntityName"/>; falls back to the
    /// <see cref="IdentityMigrationRecordMapper.WildcardTargetName"/> mapper
    /// when no exact match exists. When the wildcard mapper is also missing
    /// (misconfigured DI), a deterministic placeholder is created in-line
    /// so the importer never throws — production DI registers the wildcard
    /// mapper unconditionally.
    /// </summary>
    /// <param name="targetEntityName">Plan target-entity name.</param>
    /// <returns>The resolved mapper.</returns>
    private IMigrationRecordMapper ResolveMapper(string targetEntityName)
    {
        var concrete = _mappers.FirstOrDefault(m =>
            string.Equals(m.TargetEntityName, targetEntityName, StringComparison.Ordinal));
        if (concrete is not null)
        {
            return concrete;
        }
        var wildcard = _mappers.FirstOrDefault(m =>
            string.Equals(m.TargetEntityName, IdentityMigrationRecordMapper.WildcardTargetName, StringComparison.Ordinal));
        return wildcard ?? new IdentityMigrationRecordMapper();
    }

    /// <summary>
    /// Returns a sanitised, bounded version of <paramref name="message"/>
    /// suitable for the <c>MigrationRun.FailureReason</c> column.
    /// </summary>
    /// <param name="message">Raw exception message.</param>
    /// <returns>Bounded ≤ 1000-char string.</returns>
    private static string SanitiseFailureMessage(string message)
    {
        const int Max = 1000;
        if (string.IsNullOrEmpty(message))
        {
            return "Run failed without a description.";
        }
        return message.Length <= Max ? message : message[..(Max - 3)] + "...";
    }

    /// <summary>Writes a single audit row with a serialised details payload.</summary>
    /// <param name="eventCode">Stable event code.</param>
    /// <param name="severity">Audit severity.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="targetEntityId">Database id of the affected row.</param>
    /// <param name="details">Anonymous payload object serialised to JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completes when the audit row is enqueued.</returns>
    private async Task EmitAuditAsync(
        string eventCode,
        AuditSeverity severity,
        string actor,
        long targetEntityId,
        object details,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(details, CachedJsonOptions);
        await _audit.RecordAsync(
            eventCode,
            severity,
            actor,
            nameof(MigrationRun),
            targetEntityId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects an entity pair into its outbound summary DTO.</summary>
    /// <param name="run">Loaded run.</param>
    /// <param name="plan">Loaded plan.</param>
    /// <returns>Populated summary DTO.</returns>
    private MigrationRunSummaryDto ToSummary(MigrationRun run, MigrationPlan plan) => new(
        Id: _sqids.Encode(run.Id),
        PlanSqid: _sqids.Encode(plan.Id),
        Status: run.Status.ToString(),
        TriggerKind: run.TriggerKind.ToString(),
        TotalSourceRowsSeen: run.TotalSourceRowsSeen,
        TotalRowsImported: run.TotalRowsImported,
        TotalRowsUpdated: run.TotalRowsUpdated,
        TotalRowsSkipped: run.TotalRowsSkipped,
        TotalRowsFailed: run.TotalRowsFailed,
        IsDryRun: run.IsDryRun);

    /// <summary>Tiny tuple-like accumulator used inside <see cref="ProcessBatchAsync"/>.</summary>
    private sealed class BatchCounters
    {
        /// <summary>Rows persisted as Imported in this batch.</summary>
        public int Imported;

        /// <summary>Rows persisted as Updated in this batch.</summary>
        public int Updated;

        /// <summary>Rows the mapper marked as Skipped (idempotent no-op).</summary>
        public int Skipped;

        /// <summary>Rows that produced a Critical mapping failure.</summary>
        public int Failed;
    }
}

// Local helper to satisfy CultureInfo usage — keep imports tidy.
internal static class MigrationImporterCulture
{
    /// <summary>InvariantCulture cached for date-only formatting.</summary>
    public static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
}
