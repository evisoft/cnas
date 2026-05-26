using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Recalculation;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Recalculation;

/// <summary>
/// R1503 / TOR §3.7-D — production implementation of
/// <see cref="IMassRecalculationService"/>. Hosts the DryRun / Apply
/// triggers, lookups, the per-result reject endpoint, and the
/// apply-approved batch endpoint. Delegates heavy lifting to
/// <see cref="MassRecalculationOrchestrator"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Peak-hour gate.</b> Both <see cref="StartDryRunAsync"/> and
/// <see cref="StartApplyAsync"/> short-circuit with <see cref="ErrorCodes.Conflict"/>
/// + <see cref="ErrorCodes.PeakHourBlocked"/> when the gate signals SKIP.
/// </para>
/// </remarks>
public sealed class MassRecalculationService : IMassRecalculationService
{
    /// <summary>Stable audit code emitted when a DryRun starts.</summary>
    public const string AuditDryRunStarted = "MASS_RECALC.DRY_RUN_STARTED";

    /// <summary>Stable audit code emitted when an Apply run starts.</summary>
    public const string AuditApplyStarted = "MASS_RECALC.APPLY_STARTED";

    /// <summary>Stable audit code emitted when a result row is rejected.</summary>
    public const string AuditResultRejected = "MASS_RECALC.RESULT_REJECTED";

    /// <summary>Stable audit code emitted when the apply-approved batch completes.</summary>
    public const string AuditApprovedApplied = "MASS_RECALC.APPROVED_APPLIED";

    /// <summary>Stable conflict message when the legal-change event is not in a startable state.</summary>
    public const string EventNotStartableMessage = "LEGAL_CHANGE_EVENT_NOT_STARTABLE";

    /// <summary>Stable conflict message when rejecting a non-Computed result.</summary>
    public const string RejectOnlyComputedMessage = "RESULT_NOT_COMPUTED";

    /// <summary>Stable conflict message when applying approved on a non-Completed run.</summary>
    public const string ApplyApprovedOnlyCompletedMessage = "RUN_NOT_COMPLETED";

    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IPeakHourGate _peakHourGate;
    private readonly MassRecalculationOrchestrator _orchestrator;
    private readonly IValidator<RecalculationResultRejectInputDto> _rejectValidator;
    private readonly IValidator<RecalculationRunFilterDto> _runFilterValidator;
    private readonly IValidator<RecalculationResultFilterDto> _resultFilterValidator;

    /// <summary>Constructs the service.</summary>
    /// <param name="db">EF writer context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated-caller context.</param>
    /// <param name="audit">Audit façade.</param>
    /// <param name="peakHourGate">R2173 peak-hour gate.</param>
    /// <param name="orchestrator">Internal orchestrator instance.</param>
    /// <param name="rejectValidator">Validator for reject input.</param>
    /// <param name="runFilterValidator">Validator for run-filter input.</param>
    /// <param name="resultFilterValidator">Validator for result-filter input.</param>
    public MassRecalculationService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IPeakHourGate peakHourGate,
        MassRecalculationOrchestrator orchestrator,
        IValidator<RecalculationResultRejectInputDto> rejectValidator,
        IValidator<RecalculationRunFilterDto> runFilterValidator,
        IValidator<RecalculationResultFilterDto> resultFilterValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(peakHourGate);
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(rejectValidator);
        ArgumentNullException.ThrowIfNull(runFilterValidator);
        ArgumentNullException.ThrowIfNull(resultFilterValidator);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _peakHourGate = peakHourGate;
        _orchestrator = orchestrator;
        _rejectValidator = rejectValidator;
        _runFilterValidator = runFilterValidator;
        _resultFilterValidator = resultFilterValidator;
    }

    /// <inheritdoc />
    public Task<Result<RecalculationRunDto>> StartDryRunAsync(
        string legalChangeSqid,
        CancellationToken cancellationToken = default)
        => StartRunAsync(legalChangeSqid, RecalculationMode.DryRun, AuditDryRunStarted, cancellationToken);

    /// <inheritdoc />
    public Task<Result<RecalculationRunDto>> StartApplyAsync(
        string legalChangeSqid,
        CancellationToken cancellationToken = default)
        => StartRunAsync(legalChangeSqid, RecalculationMode.Apply, AuditApplyStarted, cancellationToken);

    /// <summary>Shared launch path for DryRun + Apply runs.</summary>
    /// <param name="legalChangeSqid">Sqid-encoded legal-change-event id.</param>
    /// <param name="mode">DryRun or Apply.</param>
    /// <param name="auditCode">Stable audit code to emit on the start event.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Run DTO on success.</returns>
    private async Task<Result<RecalculationRunDto>> StartRunAsync(
        string legalChangeSqid,
        RecalculationMode mode,
        string auditCode,
        CancellationToken cancellationToken)
    {
        // Peak-hour gate first — the orchestrator may scan a large set so the
        // gate's OffPeakOnly profile keeps it out of business hours.
        if (await _peakHourGate
                .EvaluateAsync(JobScheduleProfileRegistry.MassRecalculationApply, cancellationToken)
                .ConfigureAwait(false) == PeakHourGateDecision.Skip)
        {
            return Result<RecalculationRunDto>.Failure(
                ErrorCodes.Conflict, ErrorCodes.PeakHourBlocked);
        }

        var decoded = _sqids.TryDecode(legalChangeSqid);
        if (decoded.IsFailure)
        {
            return Result<RecalculationRunDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var evt = await _db.LegalChangeEvents
            .FirstOrDefaultAsync(e => e.Id == decoded.Value && e.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (evt is null)
        {
            return Result<RecalculationRunDto>.Failure(ErrorCodes.NotFound, "Legal-change event not found.");
        }
        if (evt.Status is LegalChangeEventStatus.Cancelled or LegalChangeEventStatus.Applied)
        {
            return Result<RecalculationRunDto>.Failure(ErrorCodes.Conflict, EventNotStartableMessage);
        }

        var actor = _caller.UserSqid ?? "system";
        var triggerKind = string.IsNullOrEmpty(_caller.UserSqid)
            ? RecalculationTriggerKind.Scheduled
            : RecalculationTriggerKind.Manual;

        // Flip event status to Recalculating while the run is in flight.
        if (evt.Status != LegalChangeEventStatus.Recalculating)
        {
            evt.Status = LegalChangeEventStatus.Recalculating;
            evt.UpdatedAtUtc = _clock.UtcNow;
            evt.UpdatedBy = actor;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var run = await _orchestrator
            .ExecuteAsync(evt, mode, triggerKind, actor, cancellationToken)
            .ConfigureAwait(false);

        // After a DryRun the event moves to ReviewPending (operator review).
        // After Apply we keep it as Recalculating; the apply-approved endpoint
        // flips it to Applied on its own.
        if (run.Status == RecalculationRunStatus.Completed && mode == RecalculationMode.DryRun)
        {
            evt.Status = LegalChangeEventStatus.ReviewPending;
            evt.UpdatedAtUtc = _clock.UtcNow;
            evt.UpdatedBy = actor;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var details = JsonSerializer.Serialize(new
        {
            runSqid = _sqids.Encode(run.Id),
            eventSqid = _sqids.Encode(evt.Id),
            mode = run.Mode.ToString(),
            triggerKind = run.TriggerKind.ToString(),
            totalScanned = run.TotalDecisionsScanned,
            totalRecalculated = run.TotalDecisionsRecalculated,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            auditCode, AuditSeverity.Critical, actor,
            nameof(RecalculationRun), run.Id, details,
            _caller.SourceIp, _caller.CorrelationId, cancellationToken).ConfigureAwait(false);

        return Result<RecalculationRunDto>.Success(ToDto(run, evt));
    }

    /// <inheritdoc />
    public async Task<Result<RecalculationRunDto>> GetRunByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<RecalculationRunDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var run = await _db.RecalculationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            return Result<RecalculationRunDto>.Failure(ErrorCodes.NotFound, "Run not found.");
        }
        var evt = await _db.LegalChangeEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == run.LegalChangeEventId, cancellationToken)
            .ConfigureAwait(false);
        return Result<RecalculationRunDto>.Success(ToDto(run, evt));
    }

    /// <inheritdoc />
    public async Task<Result<RecalculationRunDetailsDto>> GetRunDetailsAsync(
        string sqid,
        RecalculationResultFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var v = await _resultFilterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<RecalculationRunDetailsDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<RecalculationRunDetailsDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var run = await _db.RecalculationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            return Result<RecalculationRunDetailsDto>.Failure(ErrorCodes.NotFound, "Run not found.");
        }
        var evt = await _db.LegalChangeEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == run.LegalChangeEventId, cancellationToken)
            .ConfigureAwait(false);

        IQueryable<RecalculationDecisionResult> q = _db.RecalculationDecisionResults
            .AsNoTracking()
            .Where(r => r.RunId == decoded.Value && r.IsActive);

        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<RecalculationResultStatus>(filter.Status, ignoreCase: false, out var status))
        {
            q = q.Where(r => r.Status == status);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderBy(r => r.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var details = new RecalculationRunDetailsDto(
            Run: ToDto(run, evt),
            Items: rows.Select(ToDto).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take);

        return Result<RecalculationRunDetailsDto>.Success(details);
    }

    /// <inheritdoc />
    public async Task<Result<RecalculationRunPageDto>> ListRunsAsync(
        RecalculationRunFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var v = await _runFilterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<RecalculationRunPageDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<RecalculationRun> q = _db.RecalculationRuns.AsNoTracking().Where(r => r.IsActive);

        if (!string.IsNullOrWhiteSpace(filter.Mode)
            && Enum.TryParse<RecalculationMode>(filter.Mode, ignoreCase: false, out var mode))
        {
            q = q.Where(r => r.Mode == mode);
        }
        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<RecalculationRunStatus>(filter.Status, ignoreCase: false, out var status))
        {
            q = q.Where(r => r.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.LegalChangeSqid))
        {
            var d = _sqids.TryDecode(filter.LegalChangeSqid);
            if (d.IsFailure)
            {
                return Result<RecalculationRunPageDto>.Failure(d.ErrorCode!, d.ErrorMessage!);
            }
            q = q.Where(r => r.LegalChangeEventId == d.Value);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Pre-load matching events so each run carries the parent Sqid.
        var eventIds = rows.Select(r => r.LegalChangeEventId).Distinct().ToList();
        var events = await _db.LegalChangeEvents
            .AsNoTracking()
            .Where(e => eventIds.Contains(e.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byId = events.ToDictionary(e => e.Id);

        return Result<RecalculationRunPageDto>.Success(new RecalculationRunPageDto(
            Items: rows.Select(r => ToDto(r, byId.GetValueOrDefault(r.LegalChangeEventId))).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take));
    }

    /// <inheritdoc />
    public async Task<Result<RecalculationDecisionResultDto>> RejectResultAsync(
        string resultSqid,
        RecalculationResultRejectInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var v = await _rejectValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<RecalculationDecisionResultDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(resultSqid);
        if (decoded.IsFailure)
        {
            return Result<RecalculationDecisionResultDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var row = await _db.RecalculationDecisionResults
            .FirstOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<RecalculationDecisionResultDto>.Failure(ErrorCodes.NotFound, "Result not found.");
        }
        if (row.Status != RecalculationResultStatus.Computed)
        {
            return Result<RecalculationDecisionResultDto>.Failure(ErrorCodes.Conflict, RejectOnlyComputedMessage);
        }

        var actor = _caller.UserSqid ?? "system";
        var now = _clock.UtcNow;
        row.Status = RecalculationResultStatus.Rejected;
        row.Reason = input.Reason;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            resultSqid = _sqids.Encode(row.Id),
            runSqid = _sqids.Encode(row.RunId),
            benefitType = row.BenefitType,
            reason = input.Reason,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditResultRejected, AuditSeverity.Critical, actor,
            nameof(RecalculationDecisionResult), row.Id, details,
            _caller.SourceIp, _caller.CorrelationId, cancellationToken).ConfigureAwait(false);

        return Result<RecalculationDecisionResultDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<RecalculationRunDto>> ApplyApprovedResultsAsync(
        string runSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(runSqid);
        if (decoded.IsFailure)
        {
            return Result<RecalculationRunDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var run = await _db.RecalculationRuns
            .FirstOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            return Result<RecalculationRunDto>.Failure(ErrorCodes.NotFound, "Run not found.");
        }
        if (run.Status != RecalculationRunStatus.Completed)
        {
            return Result<RecalculationRunDto>.Failure(ErrorCodes.Conflict, ApplyApprovedOnlyCompletedMessage);
        }

        var actor = _caller.UserSqid ?? "system";
        await _orchestrator.ApplyApprovedAsync(run, actor, cancellationToken).ConfigureAwait(false);

        // Flip the parent event to Applied.
        var evt = await _db.LegalChangeEvents
            .FirstOrDefaultAsync(e => e.Id == run.LegalChangeEventId, cancellationToken)
            .ConfigureAwait(false);
        if (evt is not null && evt.Status != LegalChangeEventStatus.Applied)
        {
            evt.Status = LegalChangeEventStatus.Applied;
            evt.UpdatedAtUtc = _clock.UtcNow;
            evt.UpdatedBy = actor;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var details = JsonSerializer.Serialize(new
        {
            runSqid = _sqids.Encode(run.Id),
            totalApplied = run.TotalDecisionsRecalculated,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditApprovedApplied, AuditSeverity.Critical, actor,
            nameof(RecalculationRun), run.Id, details,
            _caller.SourceIp, _caller.CorrelationId, cancellationToken).ConfigureAwait(false);

        return Result<RecalculationRunDto>.Success(ToDto(run, evt));
    }

    /// <summary>Projects a run entity + parent event to the wire DTO.</summary>
    /// <param name="run">Run row.</param>
    /// <param name="evt">Parent legal-change event (may be null when soft-deleted).</param>
    /// <returns>Wire DTO.</returns>
    private RecalculationRunDto ToDto(RecalculationRun run, LegalChangeEvent? evt)
        => new(
            Id: _sqids.Encode(run.Id),
            LegalChangeSqid: evt is null ? string.Empty : _sqids.Encode(evt.Id),
            TriggerKind: run.TriggerKind.ToString(),
            Mode: run.Mode.ToString(),
            Status: run.Status.ToString(),
            StartedAt: run.StartedAt,
            CompletedAt: run.CompletedAt,
            TotalDecisionsScanned: run.TotalDecisionsScanned,
            TotalDecisionsRecalculated: run.TotalDecisionsRecalculated,
            TotalSkipped: run.TotalSkipped,
            TotalFailed: run.TotalFailed,
            TotalDeltaMdl: run.TotalDeltaMdl,
            FailureReason: run.FailureReason);

    /// <summary>Projects a result entity to the wire DTO.</summary>
    /// <param name="row">Result row.</param>
    /// <returns>Wire DTO.</returns>
    private RecalculationDecisionResultDto ToDto(RecalculationDecisionResult row)
        => new(
            Id: _sqids.Encode(row.Id),
            RunSqid: _sqids.Encode(row.RunId),
            BenefitDecisionId: row.BenefitDecisionId,
            BenefitType: row.BenefitType,
            BeneficiaryIdnpHash: row.BeneficiaryIdnpHash,
            OldAmountMdl: row.OldAmountMdl,
            NewAmountMdl: row.NewAmountMdl,
            DeltaMdl: row.DeltaMdl,
            Status: row.Status.ToString(),
            Reason: row.Reason,
            AppliedAt: row.AppliedAt);
}
