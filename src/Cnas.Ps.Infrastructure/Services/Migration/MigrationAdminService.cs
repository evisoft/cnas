using System;
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
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Migration;

/// <summary>
/// R2430 / R2431 / R2433 / TOR M4 — production implementation of
/// <see cref="IMigrationAdminService"/>. Hosts the manual-trigger entry,
/// per-run lookups, findings worklist + acknowledgement, and the
/// reconciliation report.
/// </summary>
public sealed class MigrationAdminService : IMigrationAdminService
{
    /// <summary>Cached JSON serializer options shared across audit payloads.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly IReadOnlyCnasDbContext _read;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IMigrationImporter _importer;
    private readonly IMigrationReconciler _reconciler;
    private readonly IValidator<MigrationFindingAcknowledgeInputDto> _ackValidator;
    private readonly IValidator<MigrationFindingFilterDto> _findingFilterValidator;
    private readonly IValidator<MigrationRunFilterDto> _runFilterValidator;
    private readonly IValidator<MigrationRunDetailsFilterDto> _runDetailsFilterValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="read">Read-replica context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="importer">The importer this service delegates to for manual runs.</param>
    /// <param name="reconciler">The reconciler façade.</param>
    /// <param name="ackValidator">Validator for the acknowledge-finding input.</param>
    /// <param name="findingFilterValidator">Validator for the findings list filter.</param>
    /// <param name="runFilterValidator">Validator for the runs list filter.</param>
    /// <param name="runDetailsFilterValidator">Validator for the run-details filter.</param>
    public MigrationAdminService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IMigrationImporter importer,
        IMigrationReconciler reconciler,
        IValidator<MigrationFindingAcknowledgeInputDto> ackValidator,
        IValidator<MigrationFindingFilterDto> findingFilterValidator,
        IValidator<MigrationRunFilterDto> runFilterValidator,
        IValidator<MigrationRunDetailsFilterDto> runDetailsFilterValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(reconciler);
        ArgumentNullException.ThrowIfNull(ackValidator);
        ArgumentNullException.ThrowIfNull(findingFilterValidator);
        ArgumentNullException.ThrowIfNull(runFilterValidator);
        ArgumentNullException.ThrowIfNull(runDetailsFilterValidator);
        _db = db;
        _read = read;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _importer = importer;
        _reconciler = reconciler;
        _ackValidator = ackValidator;
        _findingFilterValidator = findingFilterValidator;
        _runFilterValidator = runFilterValidator;
        _runDetailsFilterValidator = runDetailsFilterValidator;
    }

    /// <inheritdoc />
    public async Task<Result<MigrationRunSummaryDto>> TriggerManualImportAsync(
        string planSqid,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var actor = _caller.UserSqid ?? "admin";
        var trigger = dryRun ? MigrationTriggerKind.DryRun : MigrationTriggerKind.Manual;
        await _audit.RecordAsync(
            IMigrationAdminService.AuditManualImportStarted,
            AuditSeverity.Critical,
            actor,
            nameof(MigrationRun),
            null,
            JsonSerializer.Serialize(new { planSqid, dryRun, trigger = trigger.ToString() }, CachedJsonOptions),
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return await _importer.ImportAsync(planSqid, trigger, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<MigrationRunDto>> GetRunByIdAsync(
        string runSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(runSqid);
        if (decoded.IsFailure)
        {
            return Result<MigrationRunDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var run = await _read.MigrationRuns
            .FirstOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        return run is null
            ? Result<MigrationRunDto>.Failure(ErrorCodes.NotFound, "Migration run not found.")
            : Result<MigrationRunDto>.Success(ToRunDto(run));
    }

    /// <inheritdoc />
    public async Task<Result<MigrationRunDetailsDto>> GetRunDetailsAsync(
        string runSqid,
        MigrationRunDetailsFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _runDetailsFilterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<MigrationRunDetailsDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var decoded = _sqids.TryDecode(runSqid);
        if (decoded.IsFailure)
        {
            return Result<MigrationRunDetailsDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var run = await _read.MigrationRuns
            .FirstOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            return Result<MigrationRunDetailsDto>.Failure(ErrorCodes.NotFound, "Migration run not found.");
        }

        var findingsQuery = _read.MigrationFindings.Where(f => f.RunId == decoded.Value && f.IsActive);
        var findingsTotal = await findingsQuery.CountAsync(cancellationToken).ConfigureAwait(false);
        var findingsPage = await findingsQuery
            .OrderBy(f => f.BatchOrdinal)
            .ThenBy(f => f.RowOrdinalInBatch)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var batches = await _read.MigrationBatches
            .Where(b => b.RunId == decoded.Value && b.IsActive)
            .OrderBy(b => b.BatchOrdinal)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var dto = new MigrationRunDetailsDto(
            Run: ToRunDto(run),
            Findings: new MigrationFindingPageDto(
                Items: findingsPage.Select(f => ToFindingDto(f, run)).ToList(),
                Total: findingsTotal,
                Skip: filter.Skip,
                Take: filter.Take),
            Batches: batches.Select(ToBatchDto).ToList());
        return Result<MigrationRunDetailsDto>.Success(dto);
    }

    /// <inheritdoc />
    public async Task<Result<MigrationRunPageDto>> ListRunsAsync(
        MigrationRunFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _runFilterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<MigrationRunPageDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<MigrationRun> q = _read.MigrationRuns.Where(r => r.IsActive);
        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<MigrationRunStatus>(filter.Status, ignoreCase: false, out var status))
        {
            q = q.Where(r => r.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.TriggerKind)
            && Enum.TryParse<MigrationTriggerKind>(filter.TriggerKind, ignoreCase: false, out var trigger))
        {
            q = q.Where(r => r.TriggerKind == trigger);
        }
        if (!string.IsNullOrWhiteSpace(filter.PlanSqid))
        {
            var planDecoded = _sqids.TryDecode(filter.PlanSqid);
            if (planDecoded.IsFailure)
            {
                return Result<MigrationRunPageDto>.Failure(planDecoded.ErrorCode!, planDecoded.ErrorMessage!);
            }
            q = q.Where(r => r.PlanId == planDecoded.Value);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var page = new MigrationRunPageDto(
            Items: rows.Select(ToRunDto).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take);
        return Result<MigrationRunPageDto>.Success(page);
    }

    /// <inheritdoc />
    public async Task<Result<MigrationFindingDto>> AcknowledgeFindingAsync(
        string findingSqid,
        MigrationFindingAcknowledgeInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _ackValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<MigrationFindingDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var decoded = _sqids.TryDecode(findingSqid);
        if (decoded.IsFailure)
        {
            return Result<MigrationFindingDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var finding = await _db.MigrationFindings
            .FirstOrDefaultAsync(f => f.Id == decoded.Value && f.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (finding is null)
        {
            return Result<MigrationFindingDto>.Failure(ErrorCodes.NotFound, "Migration finding not found.");
        }
        if (finding.Acknowledged)
        {
            return Result<MigrationFindingDto>.Failure(ErrorCodes.Conflict, "Finding is already acknowledged.");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        finding.Acknowledged = true;
        finding.AcknowledgedAt = now;
        finding.AcknowledgedByUserId = _caller.UserId ?? 0;
        finding.AcknowledgementNote = input.Note;
        finding.UpdatedAtUtc = now;
        finding.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var run = await _read.MigrationRuns
            .FirstOrDefaultAsync(r => r.Id == finding.RunId, cancellationToken)
            .ConfigureAwait(false);

        await _audit.RecordAsync(
            IMigrationAdminService.AuditFindingAcknowledged,
            AuditSeverity.Sensitive,
            actor,
            nameof(MigrationFinding),
            finding.Id,
            JsonSerializer.Serialize(new
            {
                findingSqid = _sqids.Encode(finding.Id),
                runSqid = run is null ? null : _sqids.Encode(run.Id),
                finding.FindingCode,
                severity = finding.Severity.ToString(),
            }, CachedJsonOptions),
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return Result<MigrationFindingDto>.Success(ToFindingDto(finding, run));
    }

    /// <inheritdoc />
    public async Task<Result<MigrationFindingPageDto>> ListFindingsAsync(
        MigrationFindingFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _findingFilterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<MigrationFindingPageDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<MigrationFinding> q = _read.MigrationFindings.Where(f => f.IsActive);
        if (!string.IsNullOrWhiteSpace(filter.Severity)
            && Enum.TryParse<MigrationFindingSeverity>(filter.Severity, ignoreCase: false, out var severity))
        {
            q = q.Where(f => f.Severity == severity);
        }
        if (!string.IsNullOrWhiteSpace(filter.RunSqid))
        {
            var runDecoded = _sqids.TryDecode(filter.RunSqid);
            if (runDecoded.IsFailure)
            {
                return Result<MigrationFindingPageDto>.Failure(runDecoded.ErrorCode!, runDecoded.ErrorMessage!);
            }
            q = q.Where(f => f.RunId == runDecoded.Value);
        }
        if (!string.IsNullOrWhiteSpace(filter.FindingCode))
        {
            var code = filter.FindingCode;
            q = q.Where(f => f.FindingCode == code);
        }
        if (filter.Acknowledged.HasValue)
        {
            var ack = filter.Acknowledged.Value;
            q = q.Where(f => f.Acknowledged == ack);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(f => f.CreatedAtUtc)
            .ThenByDescending(f => f.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var runIds = rows.Select(r => r.RunId).Distinct().ToList();
        var runLookup = await _read.MigrationRuns
            .Where(r => runIds.Contains(r.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var runMap = runLookup.ToDictionary(r => r.Id, r => r);

        var items = rows
            .Select(f => ToFindingDto(f, runMap.TryGetValue(f.RunId, out var run) ? run : null))
            .ToList();
        return Result<MigrationFindingPageDto>.Success(new MigrationFindingPageDto(
            Items: items,
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take));
    }

    /// <inheritdoc />
    public async Task<Result<ReconciliationReportDto>> GetReconciliationAsync(
        string runSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(runSqid);
        if (decoded.IsFailure)
        {
            return Result<ReconciliationReportDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var run = await _read.MigrationRuns
            .FirstOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            return Result<ReconciliationReportDto>.Failure(ErrorCodes.NotFound, "Migration run not found.");
        }
        var report = await _read.ReconciliationReports
            .FirstOrDefaultAsync(r => r.RunId == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (report is null)
        {
            return Result<ReconciliationReportDto>.Failure(
                ErrorCodes.NotFound,
                "No reconciliation report exists for this run yet.");
        }
        return Result<ReconciliationReportDto>.Success(new ReconciliationReportDto(
            Id: _sqids.Encode(report.Id),
            RunSqid: _sqids.Encode(run.Id),
            Status: report.Status.ToString(),
            SourceRowCount: report.SourceRowCount,
            TargetRowCount: report.TargetRowCount,
            MissingInTargetCount: report.MissingInTargetCount,
            UnexpectedInTargetCount: report.UnexpectedInTargetCount,
            ChecksumMatchRate: report.ChecksumMatchRate,
            DiscrepancyDetailsJson: report.DiscrepancyDetailsJson,
            ComputedAt: report.ComputedAt));
    }

    /// <summary>Projects a run entity into its outbound DTO.</summary>
    /// <param name="r">Loaded run entity.</param>
    /// <returns>Populated DTO.</returns>
    private MigrationRunDto ToRunDto(MigrationRun r) => new(
        Id: _sqids.Encode(r.Id),
        PlanSqid: _sqids.Encode(r.PlanId),
        TriggerKind: r.TriggerKind.ToString(),
        Status: r.Status.ToString(),
        StartedAt: r.StartedAt,
        CompletedAt: r.CompletedAt,
        TotalSourceRowsSeen: r.TotalSourceRowsSeen,
        TotalRowsImported: r.TotalRowsImported,
        TotalRowsUpdated: r.TotalRowsUpdated,
        TotalRowsSkipped: r.TotalRowsSkipped,
        TotalRowsFailed: r.TotalRowsFailed,
        FailureReason: r.FailureReason,
        IsDryRun: r.IsDryRun);

    /// <summary>Projects a finding entity into its outbound DTO.</summary>
    /// <param name="f">Loaded finding entity.</param>
    /// <param name="run">Parent run (for Sqid encoding of the RunSqid field); may be null when the run row is missing.</param>
    /// <returns>Populated DTO.</returns>
    private MigrationFindingDto ToFindingDto(MigrationFinding f, MigrationRun? run) => new(
        Id: _sqids.Encode(f.Id),
        RunSqid: run is null ? _sqids.Encode(f.RunId) : _sqids.Encode(run.Id),
        BatchOrdinal: f.BatchOrdinal,
        RowOrdinalInBatch: f.RowOrdinalInBatch,
        Severity: f.Severity.ToString(),
        FindingCode: f.FindingCode,
        Description: f.Description,
        SourceFingerprint: f.SourceFingerprint,
        Acknowledged: f.Acknowledged,
        AcknowledgedAt: f.AcknowledgedAt,
        AcknowledgedByUserSqid: f.AcknowledgedByUserId is null or 0
            ? null
            : _sqids.Encode(f.AcknowledgedByUserId.Value),
        AcknowledgementNote: f.AcknowledgementNote);

    /// <summary>Projects a batch entity into its outbound DTO.</summary>
    /// <param name="b">Loaded batch entity.</param>
    /// <returns>Populated DTO.</returns>
    private MigrationBatchDto ToBatchDto(MigrationBatch b) => new(
        Id: _sqids.Encode(b.Id),
        BatchOrdinal: b.BatchOrdinal,
        RowsInBatch: b.RowsInBatch,
        RowsImported: b.RowsImported,
        RowsUpdated: b.RowsUpdated,
        RowsSkipped: b.RowsSkipped,
        RowsFailed: b.RowsFailed,
        DurationMs: b.DurationMs,
        ProcessedAt: b.ProcessedAt);
}
