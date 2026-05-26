using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Migration;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Migration;

/// <summary>
/// R2433 / TOR M4 — production implementation of
/// <see cref="IMigrationReconciler"/>. Computes the source-vs-staging
/// row-count delta + per-fingerprint set difference for a single
/// <see cref="MigrationRun"/> and persists the outcome as a
/// <see cref="ReconciliationReport"/> row.
/// </summary>
public sealed class MigrationReconciler : IMigrationReconciler
{
    /// <summary>Maximum number of discrepancy entries serialised into <c>DiscrepancyDetailsJson</c>.</summary>
    public const int MaxDiscrepancyEntries = 100;

    /// <summary>Cached JSON serializer options shared across audit + discrepancy payloads.</summary>
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

    /// <summary>Constructs the reconciler with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="sources">Registered source adapters; resolved by SourceKind.</param>
    public MigrationReconciler(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IEnumerable<IMigrationSource> sources)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(sources);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _sources = sources;
    }

    /// <inheritdoc />
    public async Task<Result<ReconciliationReportDto>> ReconcileAsync(
        string runSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(runSqid);
        if (decoded.IsFailure)
        {
            return Result<ReconciliationReportDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var run = await _db.MigrationRuns
            .FirstOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            return Result<ReconciliationReportDto>.Failure(
                IMigrationReconciler.RunNotFoundCode,
                "Migration run not found.");
        }

        var plan = await _db.MigrationPlans
            .FirstOrDefaultAsync(p => p.Id == run.PlanId, cancellationToken)
            .ConfigureAwait(false);
        if (plan is null)
        {
            return Result<ReconciliationReportDto>.Failure(
                IMigrationImporter.PlanNotFoundCode,
                "Parent migration plan not found.");
        }

        // Compute source count + source fingerprint set.
        long sourceCount;
        HashSet<string> sourceFingerprints;
        var source = _sources.FirstOrDefault(s => s.SourceKind == plan.SourceKind);
        if (source is null)
        {
            sourceCount = 0;
            sourceFingerprints = new HashSet<string>(StringComparer.Ordinal);
        }
        else
        {
            sourceCount = await source.CountAsync(plan, cancellationToken).ConfigureAwait(false);
            sourceFingerprints = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var record in source.StreamAsync(plan, cancellationToken).ConfigureAwait(false))
            {
                sourceFingerprints.Add(record.SourceFingerprint);
            }
        }

        // Compute staging fingerprint set.
        var stagingFingerprints = await _db.MigrationStagingRows
            .Where(r => r.RunId == run.Id && r.IsActive)
            .Select(r => r.SourceFingerprint)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var stagingSet = new HashSet<string>(stagingFingerprints, StringComparer.Ordinal);
        var targetCount = (long)stagingFingerprints.Count;

        var missingInTarget = sourceFingerprints.Where(fp => !stagingSet.Contains(fp)).ToList();
        var unexpectedInTarget = stagingSet.Where(fp => !sourceFingerprints.Contains(fp)).ToList();

        var matchedCount = sourceFingerprints.Count - missingInTarget.Count;
        var matchRate = sourceFingerprints.Count == 0
            ? 1.0m
            : (decimal)matchedCount / sourceFingerprints.Count;
        matchRate = Math.Round(matchRate, 4, MidpointRounding.AwayFromZero);

        ReconciliationStatus status;
        if (missingInTarget.Count == 0 && unexpectedInTarget.Count == 0)
        {
            status = ReconciliationStatus.Passed;
        }
        else
        {
            status = ReconciliationStatus.Discrepancy;
        }

        var discrepancies = missingInTarget
            .Select(fp => new { kind = "missing", fingerprint = fp })
            .Cast<object>()
            .Concat(unexpectedInTarget.Select(fp => new { kind = "unexpected", fingerprint = fp }))
            .Take(MaxDiscrepancyEntries)
            .ToList();
        string? discrepancyJson = discrepancies.Count == 0
            ? null
            : JsonSerializer.Serialize(discrepancies, CachedJsonOptions);
        if (discrepancyJson is not null && discrepancyJson.Length > 16384)
        {
            discrepancyJson = discrepancyJson[..(16384 - 3)] + "...";
        }

        // Upsert the reconciliation report row.
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "system";
        var existing = await _db.ReconciliationReports
            .FirstOrDefaultAsync(r => r.RunId == run.Id && r.IsActive, cancellationToken)
            .ConfigureAwait(false);

        ReconciliationReport report;
        if (existing is null)
        {
            report = new ReconciliationReport
            {
                RunId = run.Id,
                Status = status,
                SourceRowCount = sourceCount,
                TargetRowCount = targetCount,
                MissingInTargetCount = missingInTarget.Count,
                UnexpectedInTargetCount = unexpectedInTarget.Count,
                ChecksumMatchRate = matchRate,
                DiscrepancyDetailsJson = discrepancyJson,
                ComputedAt = now,
                CreatedAtUtc = now,
                CreatedBy = actor,
                IsActive = true,
            };
            _db.ReconciliationReports.Add(report);
        }
        else
        {
            report = existing;
            report.Status = status;
            report.SourceRowCount = sourceCount;
            report.TargetRowCount = targetCount;
            report.MissingInTargetCount = missingInTarget.Count;
            report.UnexpectedInTargetCount = unexpectedInTarget.Count;
            report.ChecksumMatchRate = matchRate;
            report.DiscrepancyDetailsJson = discrepancyJson;
            report.ComputedAt = now;
            report.UpdatedAtUtc = now;
            report.UpdatedBy = actor;
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            IMigrationReconciler.AuditReconciliationComputed,
            AuditSeverity.Critical,
            actor,
            nameof(ReconciliationReport),
            report.Id,
            JsonSerializer.Serialize(new
            {
                runSqid = _sqids.Encode(run.Id),
                planSqid = _sqids.Encode(plan.Id),
                status = status.ToString(),
                sourceCount,
                targetCount,
                missing = missingInTarget.Count,
                unexpected = unexpectedInTarget.Count,
            }, CachedJsonOptions),
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        CnasMeter.MigrationReconciliationOutcome.Add(
            1,
            new KeyValuePair<string, object?>("target_entity", plan.TargetEntityName),
            new KeyValuePair<string, object?>("status", status.ToString()));

        return Result<ReconciliationReportDto>.Success(ToDto(report, run));
    }

    /// <summary>Projects an entity pair into its outbound DTO.</summary>
    /// <param name="report">Loaded reconciliation report.</param>
    /// <param name="run">Loaded run.</param>
    /// <returns>Populated DTO.</returns>
    private ReconciliationReportDto ToDto(ReconciliationReport report, MigrationRun run) => new(
        Id: _sqids.Encode(report.Id),
        RunSqid: _sqids.Encode(run.Id),
        Status: report.Status.ToString(),
        SourceRowCount: report.SourceRowCount,
        TargetRowCount: report.TargetRowCount,
        MissingInTargetCount: report.MissingInTargetCount,
        UnexpectedInTargetCount: report.UnexpectedInTargetCount,
        ChecksumMatchRate: report.ChecksumMatchRate,
        DiscrepancyDetailsJson: report.DiscrepancyDetailsJson,
        ComputedAt: report.ComputedAt);
}
