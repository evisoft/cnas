using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Application.Workflow;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.Workflow;

/// <summary>
/// R0125 / CF 16.09 — default <see cref="IWorkflowTaskHistoryService"/> implementation
/// backed by <see cref="ICnasDbContext"/>. Records lifecycle events on the append-only
/// <see cref="WorkflowTaskStepHistory"/> projection and reads chronologically-ordered
/// pages back through the same DB.
/// </summary>
/// <remarks>
/// <para>
/// <b>Writer-site invariant.</b> Every site that mutates a <see cref="WorkflowTask"/>
/// should call <see cref="RecordEventAsync"/> with the matching transition kind. The
/// service does NOT itself observe DB-context change notifications — it relies on
/// explicit calls so the audit row + counter increment are deterministic.
/// </para>
/// <para>
/// <b>Audit row.</b> Every recorded event emits a stable
/// <c>WORKFLOW_TASK.HISTORY_RECORDED</c> audit at Information severity. The
/// projection itself is the durable artifact; the audit row is the cross-subsystem
/// correlator (lets investigators jump from an audit trail to the history projection
/// without joining tables).
/// </para>
/// </remarks>
/// <param name="db">Per-request EF Core context.</param>
/// <param name="sqids">Sqid encoder/decoder.</param>
/// <param name="clock">Time provider — <c>DateTime.UtcNow</c> is forbidden.</param>
/// <param name="caller">Active caller context (actor + correlation).</param>
/// <param name="audit">Centralised audit-writer facade.</param>
/// <param name="filterValidator">Filter validator used by the read path.</param>
/// <param name="logger">Structured logger.</param>
public sealed class WorkflowTaskHistoryService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IAuditService audit,
    FluentValidation.IValidator<WorkflowTaskHistoryFilterDto> filterValidator,
    ILogger<WorkflowTaskHistoryService> logger)
    : IWorkflowTaskHistoryService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;
    private readonly FluentValidation.IValidator<WorkflowTaskHistoryFilterDto> _filterValidator = filterValidator;
    private readonly ILogger<WorkflowTaskHistoryService> _logger = logger;

    /// <summary>Stable audit event code.</summary>
    private const string AuditEventCode = "WORKFLOW_TASK.HISTORY_RECORDED";

    /// <summary>Default page size when caller does not supply <c>Take</c>.</summary>
    private const int DefaultTake = 50;

    /// <inheritdoc />
    public async System.Threading.Tasks.Task<Result> RecordEventAsync(
        long workflowTaskId,
        WorkflowTaskStepEventKind eventKind,
        string stepCode,
        long? actorUserId,
        string? decisionCode,
        string? note,
        System.Threading.CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stepCode))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "StepCode is required.");
        }
        if (stepCode.Length > 64)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "StepCode exceeds the 64-character cap.");
        }
        if (decisionCode is not null && decisionCode.Length > 64)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "DecisionCode exceeds the 64-character cap.");
        }
        if (note is not null && note.Length > 1000)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Note exceeds the 1000-character cap.");
        }

        var occurredAt = _clock.UtcNow;
        var row = new WorkflowTaskStepHistory
        {
            WorkflowTaskId = workflowTaskId,
            StepCode = stepCode,
            EventKind = eventKind,
            OccurredAt = occurredAt,
            ActorUserId = actorUserId,
            DecisionCode = decisionCode,
            Note = note,
            CreatedAtUtc = occurredAt,
            CreatedBy = _caller.UserSqid ?? "system",
            IsActive = true,
        };

        try
        {
            _db.WorkflowTaskStepHistories.Add(row);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex,
                "Failed to persist workflow-task history event for task {TaskId}.",
                workflowTaskId);
            return Result.Failure(
                ErrorCodes.WorkflowTaskHistoryFailed,
                "Could not persist workflow-task history row.");
        }

        CnasMeter.WorkflowTaskHistoryEvent.Add(1,
            new KeyValuePair<string, object?>("event_kind", eventKind.ToString()));

        var details = JsonSerializer.Serialize(new
        {
            workflowTaskSqid = _sqids.Encode(workflowTaskId),
            stepCode,
            eventKind = eventKind.ToString(),
            decisionCode,
            actorSqid = actorUserId is null ? null : _sqids.Encode(actorUserId.Value),
        });

        await _audit.RecordAsync(
            eventCode: AuditEventCode,
            severity: AuditSeverity.Information,
            actorId: _caller.UserSqid ?? "system",
            targetEntity: nameof(WorkflowTask),
            targetEntityId: workflowTaskId,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async System.Threading.Tasks.Task<Result<WorkflowTaskHistoryPageDto>> GetHistoryAsync(
        string workflowTaskSqid,
        WorkflowTaskHistoryFilterDto filter,
        System.Threading.CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var decoded = _sqids.TryDecode(workflowTaskSqid);
        if (decoded.IsFailure)
        {
            return Result<WorkflowTaskHistoryPageDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var validation = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<WorkflowTaskHistoryPageDto>.Failure(
                ErrorCodes.ValidationFailed, validation.ToString("; "));
        }

        var skip = Math.Max(0, filter.Skip ?? 0);
        var take = Math.Clamp(filter.Take ?? DefaultTake, 1, WorkflowTaskHistoryFilterDtoValidator.MaxTake);

        var query = _db.WorkflowTaskStepHistories
            .Where(h => h.WorkflowTaskId == decoded.Value && h.IsActive);

        if (!string.IsNullOrEmpty(filter.EventKind)
            && Enum.TryParse<WorkflowTaskStepEventKind>(filter.EventKind, ignoreCase: false, out var kindFilter))
        {
            query = query.Where(h => h.EventKind == kindFilter);
        }

        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await query
            .OrderBy(h => h.OccurredAt)
            .Skip(skip).Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<WorkflowTaskStepHistoryDto> items = rows
            .Select(h => new WorkflowTaskStepHistoryDto(
                Id: _sqids.Encode(h.Id),
                WorkflowTaskSqid: _sqids.Encode(h.WorkflowTaskId),
                StepCode: h.StepCode,
                EventKind: h.EventKind.ToString(),
                OccurredAt: h.OccurredAt,
                ActorUserSqid: h.ActorUserId is null ? null : _sqids.Encode(h.ActorUserId.Value),
                DecisionCode: h.DecisionCode,
                Note: h.Note))
            .ToList();

        return Result<WorkflowTaskHistoryPageDto>.Success(
            new WorkflowTaskHistoryPageDto(items, total));
    }
}
