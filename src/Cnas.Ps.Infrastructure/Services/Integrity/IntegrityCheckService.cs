using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Integrity;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Integrity;

/// <summary>
/// R2282 / TOR SEC 036 — production implementation of
/// <see cref="IIntegrityCheckService"/>. Manages the manual-trigger entry,
/// per-run lookups, finding acknowledgement, and the open-findings page
/// projection.
/// </summary>
public sealed class IntegrityCheckService : IIntegrityCheckService
{
    private readonly ICnasDbContext _db;
    private readonly IIntegrityCheckContext _checkContext;
    private readonly IEnumerable<IIntegrityCheck> _checks;
    private readonly IAuditService _audit;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IValidator<IntegrityFindingFilterDto> _filterValidator;
    private readonly IValidator<IntegrityFindingAcknowledgeInputDto> _ackValidator;

    /// <summary>Maximum permitted recent-runs <c>take</c> on <see cref="ListRecentRunsAsync"/>.</summary>
    public const int MaxRecentRunsTake = 100;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer DB context — used to persist runs + findings + acknowledgements.</param>
    /// <param name="checkContext">Read-only context handed to each registered check.</param>
    /// <param name="checks">All registered <see cref="IIntegrityCheck"/> implementations.</param>
    /// <param name="audit">Audit service — emits the manual-run + acknowledgement rows.</param>
    /// <param name="sqids">Sqid encoder/decoder for boundary id translation.</param>
    /// <param name="clock">UTC clock abstraction (CLAUDE.md RULE 4).</param>
    /// <param name="caller">Caller context used to attribute the manual run + acknowledgement.</param>
    /// <param name="filterValidator">Validator for the filter envelope.</param>
    /// <param name="ackValidator">Validator for the acknowledgement payload.</param>
    public IntegrityCheckService(
        ICnasDbContext db,
        IIntegrityCheckContext checkContext,
        IEnumerable<IIntegrityCheck> checks,
        IAuditService audit,
        ISqidService sqids,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IValidator<IntegrityFindingFilterDto> filterValidator,
        IValidator<IntegrityFindingAcknowledgeInputDto> ackValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(checkContext);
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(filterValidator);
        ArgumentNullException.ThrowIfNull(ackValidator);
        _db = db;
        _checkContext = checkContext;
        _checks = checks;
        _audit = audit;
        _sqids = sqids;
        _clock = clock;
        _caller = caller;
        _filterValidator = filterValidator;
        _ackValidator = ackValidator;
    }

    /// <inheritdoc />
    public async Task<Result<IntegrityCheckRunDto>> StartManualRunAsync(CancellationToken cancellationToken = default)
    {
        var actor = _caller.UserSqid ?? "admin";
        var run = await ExecuteRunAsync(IntegrityCheckTriggerKind.Manual, actor, cancellationToken)
            .ConfigureAwait(false);

        // Critical audit — operator-initiated runs are high-trust events.
        var details = JsonSerializer.Serialize(new
        {
            runId = run.Id,
            triggerKind = nameof(IntegrityCheckTriggerKind.Manual),
            totalRowsScanned = run.TotalRowsScanned,
            totalFindings = run.TotalFindings,
        });
        await _audit.RecordAsync(
            "INTEGRITY_CHECK.MANUAL_RUN_STARTED",
            AuditSeverity.Critical,
            actor,
            nameof(IntegrityCheckRun),
            run.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return Result<IntegrityCheckRunDto>.Success(ToDto(run));
    }

    /// <inheritdoc />
    public async Task<Result<IntegrityCheckRunDto>> GetRunByIdAsync(string sqid, CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<IntegrityCheckRunDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var run = await _db.IntegrityCheckRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            return Result<IntegrityCheckRunDto>.Failure(ErrorCodes.NotFound, "Integrity-check run not found.");
        }
        return Result<IntegrityCheckRunDto>.Success(ToDto(run));
    }

    /// <inheritdoc />
    public async Task<Result<IntegrityCheckRunDetailsDto>> GetRunDetailsAsync(string sqid, CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<IntegrityCheckRunDetailsDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var run = await _db.IntegrityCheckRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            return Result<IntegrityCheckRunDetailsDto>.Failure(ErrorCodes.NotFound, "Integrity-check run not found.");
        }
        var findings = await _db.IntegrityCheckFindings
            .AsNoTracking()
            .Where(f => f.RunId == decoded.Value && f.IsActive)
            .OrderBy(f => f.FirstDetectedAt)
            .ThenBy(f => f.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var dto = new IntegrityCheckRunDetailsDto(ToDto(run), findings.Select(ToDto).ToList());
        return Result<IntegrityCheckRunDetailsDto>.Success(dto);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<IntegrityCheckRunDto>>> ListRecentRunsAsync(int take, CancellationToken cancellationToken = default)
    {
        if (take < 1 || take > MaxRecentRunsTake)
        {
            return Result<IReadOnlyList<IntegrityCheckRunDto>>.Failure(
                ErrorCodes.ValidationFailed,
                $"take must be in 1..{MaxRecentRunsTake}.");
        }
        var rows = await _db.IntegrityCheckRuns
            .AsNoTracking()
            .Where(r => r.IsActive)
            .OrderByDescending(r => r.RunStartedAt)
            .ThenByDescending(r => r.Id)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return Result<IReadOnlyList<IntegrityCheckRunDto>>.Success(rows.Select(ToDto).ToList());
    }

    /// <inheritdoc />
    public async Task<Result<IntegrityCheckFindingDto>> AcknowledgeFindingAsync(
        string findingSqid,
        IntegrityFindingAcknowledgeInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ack = await _ackValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!ack.IsValid)
        {
            return Result<IntegrityCheckFindingDto>.Failure(ErrorCodes.ValidationFailed, ack.ToString());
        }

        var decoded = _sqids.TryDecode(findingSqid);
        if (decoded.IsFailure)
        {
            return Result<IntegrityCheckFindingDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var finding = await _db.IntegrityCheckFindings
            .FirstOrDefaultAsync(f => f.Id == decoded.Value && f.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (finding is null)
        {
            return Result<IntegrityCheckFindingDto>.Failure(ErrorCodes.NotFound, "Integrity-check finding not found.");
        }
        if (finding.Acknowledged)
        {
            return Result<IntegrityCheckFindingDto>.Failure(ErrorCodes.Conflict, "Finding is already acknowledged.");
        }

        var now = _clock.UtcNow;
        finding.Acknowledged = true;
        finding.AcknowledgedAt = now;
        finding.AcknowledgedByUserId = _caller.UserId;
        finding.AcknowledgementNote = input.Note;
        finding.UpdatedAtUtc = now;
        finding.UpdatedBy = _caller.UserSqid ?? "admin";
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            findingId = finding.Id,
            runId = finding.RunId,
            checkCode = finding.CheckCode,
            severity = finding.Severity.ToString(),
            aggregateName = finding.AggregateName,
            aggregateRowId = finding.AggregateRowId,
        });
        await _audit.RecordAsync(
            "INTEGRITY_CHECK.FINDING_ACKNOWLEDGED",
            AuditSeverity.Critical,
            _caller.UserSqid ?? "admin",
            nameof(IntegrityCheckFinding),
            finding.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return Result<IntegrityCheckFindingDto>.Success(ToDto(finding));
    }

    /// <inheritdoc />
    public async Task<Result<IntegrityFindingPageDto>> ListOpenFindingsAsync(
        IntegrityFindingFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<IntegrityFindingPageDto>.Failure(ErrorCodes.ValidationFailed, v.ToString());
        }

        IQueryable<IntegrityCheckFinding> query = _db.IntegrityCheckFindings
            .AsNoTracking()
            .Where(f => f.IsActive);

        if (filter.OnlyOpen)
        {
            query = query.Where(f => !f.Acknowledged);
        }
        if (!string.IsNullOrWhiteSpace(filter.AggregateName))
        {
            query = query.Where(f => f.AggregateName == filter.AggregateName);
        }
        if (!string.IsNullOrWhiteSpace(filter.CheckCode))
        {
            query = query.Where(f => f.CheckCode == filter.CheckCode);
        }
        if (!string.IsNullOrWhiteSpace(filter.Severity)
            && Enum.TryParse<IntegrityFindingSeverity>(filter.Severity, ignoreCase: false, out var sev))
        {
            query = query.Where(f => f.Severity == sev);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await query
            .OrderByDescending(f => f.FirstDetectedAt)
            .ThenByDescending(f => f.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var page = new IntegrityFindingPageDto(
            Items: rows.Select(ToDto).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take);
        return Result<IntegrityFindingPageDto>.Success(page);
    }

    /// <summary>
    /// Internal hot path shared by the manual-trigger entry and the scheduled
    /// job. Creates a Running row, iterates every check, persists findings,
    /// and finalises the run to Completed or Failed.
    /// </summary>
    /// <param name="triggerKind">Origin of the run.</param>
    /// <param name="actorId">Audit-attribution identifier (caller Sqid or <c>"system"</c>).</param>
    /// <param name="cancellationToken">Cancellation propagated from the caller.</param>
    /// <returns>The persisted run row.</returns>
    public async Task<IntegrityCheckRun> ExecuteRunAsync(
        IntegrityCheckTriggerKind triggerKind,
        string actorId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        var now = _clock.UtcNow;
        var run = new IntegrityCheckRun
        {
            RunStartedAt = now,
            TriggerKind = triggerKind,
            Status = IntegrityCheckRunStatus.Running,
            CreatedAtUtc = now,
            CreatedBy = actorId,
            IsActive = true,
        };
        _db.IntegrityCheckRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        long rowsScanned = 0;
        var findingRecords = new List<IntegrityCheckFindingRecord>();
        try
        {
            foreach (var check in _checks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var partial = await check.RunAsync(_checkContext, cancellationToken).ConfigureAwait(false);
                rowsScanned += partial.RowsScanned;
                findingRecords.AddRange(partial.Findings);
            }

            var completionTime = _clock.UtcNow;
            foreach (var record in findingRecords)
            {
                _db.IntegrityCheckFindings.Add(new IntegrityCheckFinding
                {
                    RunId = run.Id,
                    CheckCode = record.CheckCode,
                    Severity = record.Severity,
                    AggregateName = record.AggregateName,
                    AggregateRowId = record.AggregateRowId,
                    Description = record.Description,
                    ExpectedValue = record.ExpectedValue,
                    ActualValue = record.ActualValue,
                    FirstDetectedAt = completionTime,
                    Acknowledged = false,
                    CreatedAtUtc = completionTime,
                    CreatedBy = actorId,
                    IsActive = true,
                });
            }

            var bySeverity = findingRecords
                .GroupBy(r => r.Severity)
                .ToDictionary(g => g.Key.ToString(), g => g.Count(), StringComparer.Ordinal);
            // Ensure every severity is represented for chart stability.
            foreach (var severity in Enum.GetNames<IntegrityFindingSeverity>())
            {
                bySeverity.TryAdd(severity, 0);
            }

            run.RunCompletedAt = completionTime;
            run.Status = IntegrityCheckRunStatus.Completed;
            run.TotalRowsScanned = rowsScanned;
            run.TotalFindings = findingRecords.Count;
            run.FindingsBySeverity = JsonSerializer.Serialize(bySeverity);
            run.UpdatedAtUtc = completionTime;
            run.UpdatedBy = actorId;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return run;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mark the run Failed but do not rethrow — operators need the
            // partial-progress record for the post-mortem. The Critical audit
            // is emitted by the job-level handler.
            run.RunCompletedAt = _clock.UtcNow;
            run.Status = IntegrityCheckRunStatus.Failed;
            run.TotalRowsScanned = rowsScanned;
            run.TotalFindings = findingRecords.Count;
            run.FailureReason = ex.GetType().Name + ": " + ex.Message;
            run.UpdatedAtUtc = _clock.UtcNow;
            run.UpdatedBy = actorId;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return run;
        }
    }

    /// <summary>Translates a persisted <see cref="IntegrityCheckRun"/> to its DTO projection.</summary>
    /// <param name="run">Persisted run row.</param>
    /// <returns>The wire DTO.</returns>
    private IntegrityCheckRunDto ToDto(IntegrityCheckRun run)
    {
        var bySeverity = ParseSeverityDictionary(run.FindingsBySeverity);
        return new IntegrityCheckRunDto(
            Id: _sqids.Encode(run.Id),
            RunStartedAt: run.RunStartedAt,
            RunCompletedAt: run.RunCompletedAt,
            TriggerKind: run.TriggerKind.ToString(),
            Status: run.Status.ToString(),
            TotalRowsScanned: run.TotalRowsScanned,
            TotalFindings: run.TotalFindings,
            FindingsBySeverity: bySeverity,
            FailureReason: run.FailureReason);
    }

    /// <summary>Translates a persisted <see cref="IntegrityCheckFinding"/> to its DTO projection.</summary>
    /// <param name="finding">Persisted finding row.</param>
    /// <returns>The wire DTO.</returns>
    private IntegrityCheckFindingDto ToDto(IntegrityCheckFinding finding)
        => new(
            Id: _sqids.Encode(finding.Id),
            RunSqid: _sqids.Encode(finding.RunId),
            CheckCode: finding.CheckCode,
            Severity: finding.Severity.ToString(),
            AggregateName: finding.AggregateName,
            AggregateRowId: finding.AggregateRowId,
            Description: finding.Description,
            ExpectedValue: finding.ExpectedValue,
            ActualValue: finding.ActualValue,
            FirstDetectedAt: finding.FirstDetectedAt,
            Acknowledged: finding.Acknowledged,
            AcknowledgedAt: finding.AcknowledgedAt,
            AcknowledgedByUserSqid: finding.AcknowledgedByUserId is { } uid ? _sqids.Encode(uid) : null,
            AcknowledgementNote: finding.AcknowledgementNote);

    /// <summary>
    /// Parses a stored JSON severity-count map back to a typed dictionary,
    /// tolerating null/malformed inputs by returning an empty dictionary.
    /// </summary>
    /// <param name="json">Stored JSON string (or null).</param>
    /// <returns>A dictionary; never null.</returns>
    private static IReadOnlyDictionary<string, int> ParseSeverityDictionary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            return dict ?? new Dictionary<string, int>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }
}
