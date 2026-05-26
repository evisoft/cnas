using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Application.BulkActions.Operations;

/// <summary>
/// R0527 / TOR CF 03.11 / UI 015 — parameterless bulk operation that marks every
/// selected <see cref="WorkflowTask"/> as <see cref="WorkflowTaskStatus.Completed"/>
/// and stamps <see cref="WorkflowTask.CompletedAtUtc"/>. Designed for the bulk
/// "close out the inbox" workflow during shift handover (operator confirms all
/// selected tasks done; no per-row detail required).
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency.</b> Rows that are already in <see cref="WorkflowTaskStatus.Completed"/>
/// or <see cref="WorkflowTaskStatus.Cancelled"/> are no-op successes (the runner counts
/// them as succeeded). This matches the bulk-action UI affordance where the operator
/// may have a stale page and would otherwise get spurious "already done" failures.
/// </para>
/// <para>
/// <b>Audit emission.</b> Emits one <c>WORKFLOWTASK.COMPLETED</c> audit row (severity
/// Notice) per row that was actually transitioned. No-op rows do NOT emit audit so the
/// audit trail stays meaningful.
/// </para>
/// </remarks>
public sealed class WorkflowTaskMarkCompleteBulkOperation : IBulkOperation
{
    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly IAuditService _audit;

    /// <summary>Creates the operation.</summary>
    /// <param name="db">Per-request CNAS DbContext.</param>
    /// <param name="clock">UTC clock used to stamp <c>CompletedAtUtc</c> and audit rows.</param>
    /// <param name="audit">Audit-service façade used for per-row WORKFLOWTASK.COMPLETED rows.</param>
    public WorkflowTaskMarkCompleteBulkOperation(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        IAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(audit);

        _db = db;
        _clock = clock;
        _audit = audit;
    }

    /// <summary>Stable operation code.</summary>
    public const string OperationCode = "WorkflowTask.MarkComplete";

    /// <inheritdoc />
    public string Code => OperationCode;

    /// <inheritdoc />
    public string Registry => BulkRegistries.WorkflowTask;

    /// <inheritdoc />
    public string RequiredPermission => "WorkflowTask.Manage";

    /// <inheritdoc />
    public int MaxRowsPerRun => 1_000;

    /// <inheritdoc />
    public bool RequiresParameters => false;

    /// <inheritdoc />
    public async Task<BulkRowOutcome> ExecuteAsync(
        long rowId,
        string? parametersJson,
        ICallerContext caller,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        _ = parametersJson; // parameterless operation; suppress the unused warning.

        var task = await _db.WorkflowTasks
            .SingleOrDefaultAsync(t => t.Id == rowId && t.IsActive, ct)
            .ConfigureAwait(false);
        if (task is null)
        {
            return BulkRowOutcome.Failed(ErrorCodes.NotFound, "Workflow task not found.");
        }

        // Already in a terminal status — no-op success (see remarks on idempotency).
        if (task.Status is WorkflowTaskStatus.Completed or WorkflowTaskStatus.Cancelled)
        {
            return BulkRowOutcome.Succeeded();
        }

        var now = _clock.UtcNow;
        task.Status = WorkflowTaskStatus.Completed;
        task.CompletedAtUtc = now;
        task.UpdatedAtUtc = now;
        task.UpdatedBy = caller.UserSqid;
        // Completing the task clears the unclaimed-pool stamp per the R0202 invariant.
        task.UnclaimedSinceUtc = null;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var detailsJson = JsonSerializer.Serialize(new
        {
            completedAtUtc = now,
        });
        await _audit.RecordAsync(
            eventCode: "WORKFLOWTASK.COMPLETED",
            severity: AuditSeverity.Notice,
            actorId: caller.UserSqid ?? "system",
            targetEntity: nameof(WorkflowTask),
            targetEntityId: task.Id,
            detailsJson: detailsJson,
            sourceIp: caller.SourceIp,
            correlationId: caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);

        return BulkRowOutcome.Succeeded();
    }
}
