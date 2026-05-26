using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Application.BulkActions.Operations;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — sample bulk operation reassigning every selected
/// <see cref="WorkflowTask"/> to a new assignee. Lives in the Application layer
/// (depends on <see cref="ICnasDbContext"/> + <see cref="IAuditService"/>); registered
/// in DI as a scoped <see cref="IBulkOperation"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parameters shape.</b> <c>{ "newAssigneeSqid": "&lt;sqid&gt;" }</c>. The runner
/// guarantees <c>parametersJson</c> is non-null when this operation runs (the
/// runner pre-checks <see cref="RequiresParameters"/>). The Sqid is decoded inside
/// <see cref="ExecuteAsync"/> on every row — the decode is cheap and per-row
/// independence keeps the operation stateless.
/// </para>
/// <para>
/// <b>Audit emission.</b> The operation emits a <c>WORKFLOWTASK.REASSIGNED</c>
/// audit row (severity Notice) per successfully reassigned task. The runner's own
/// START/END audit rows wrap the operation's per-row audits.
/// </para>
/// <para>
/// <b>Failure modes.</b>
/// <list type="bullet">
///   <item><description><c>VALIDATION_FAILED</c> when the parameters shape is malformed.</description></item>
///   <item><description><c>NOT_FOUND</c> when the row no longer exists.</description></item>
///   <item><description><c>ALREADY_COMPLETED</c> when the task is in
///   <see cref="WorkflowTaskStatus.Completed"/> or
///   <see cref="WorkflowTaskStatus.Cancelled"/>.</description></item>
///   <item><description><c>NOT_FOUND</c> when the new assignee does not exist (re-using
///   the stable code so the UI surfaces it as "user not found").</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class WorkflowTaskReassignBulkOperation : IBulkOperation
{
    private readonly ICnasDbContext _db;
    private readonly ISqidService _sqids;
    private readonly IAuditService _audit;
    private readonly ICnasTimeProvider _clock;

    /// <summary>Creates the operation.</summary>
    /// <param name="db">Per-request CNAS DbContext.</param>
    /// <param name="sqids">Sqid service used to decode the new-assignee parameter.</param>
    /// <param name="audit">Audit-service façade used for per-row WORKFLOWTASK.REASSIGNED rows.</param>
    /// <param name="clock">UTC clock used to stamp <c>UpdatedAtUtc</c>.</param>
    public WorkflowTaskReassignBulkOperation(
        ICnasDbContext db,
        ISqidService sqids,
        IAuditService audit,
        ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _sqids = sqids;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>Stable operation code.</summary>
    public const string OperationCode = "WorkflowTask.Reassign";

    /// <summary>
    /// Cached <see cref="JsonSerializerOptions"/> reused on every row to satisfy
    /// the CA1869 analyzer guidance — building a fresh options instance per
    /// deserialise call is expensive and unnecessary.
    /// </summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public string Code => OperationCode;

    /// <inheritdoc />
    public string Registry => BulkRegistries.WorkflowTask;

    /// <inheritdoc />
    public string RequiredPermission => "WorkflowTask.Manage";

    /// <inheritdoc />
    public int MaxRowsPerRun => 1_000;

    /// <inheritdoc />
    public bool RequiresParameters => true;

    /// <inheritdoc />
    public async Task<BulkRowOutcome> ExecuteAsync(
        long rowId,
        string? parametersJson,
        ICallerContext caller,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        // Re-parse the parameters per row — cheap, keeps the operation stateless,
        // and means a bad shape surfaces as a per-row failure rather than aborting
        // the whole run.
        Parameters? parameters;
        try
        {
            parameters = JsonSerializer.Deserialize<Parameters>(
                parametersJson ?? "null",
                CachedJsonOptions);
        }
        catch (JsonException ex)
        {
            return BulkRowOutcome.Failed(ErrorCodes.ValidationFailed, $"Parameters JSON malformed: {ex.Message}");
        }
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.NewAssigneeSqid))
        {
            return BulkRowOutcome.Failed(
                ErrorCodes.ValidationFailed,
                "Parameters must carry a non-empty newAssigneeSqid.");
        }
        var decoded = _sqids.TryDecode(parameters.NewAssigneeSqid);
        if (decoded.IsFailure)
        {
            return BulkRowOutcome.Failed(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var newAssigneeId = decoded.Value;

        // Verify the assignee exists + is active. Cheap query — the row count is
        // bounded by MaxRowsPerRun (1 000) so we do not bother batching the lookup.
        var assigneeExists = await _db.UserProfiles
            .AnyAsync(u => u.Id == newAssigneeId && u.IsActive, ct)
            .ConfigureAwait(false);
        if (!assigneeExists)
        {
            return BulkRowOutcome.Failed(ErrorCodes.NotFound, "New assignee user does not exist or is inactive.");
        }

        var task = await _db.WorkflowTasks
            .SingleOrDefaultAsync(t => t.Id == rowId && t.IsActive, ct)
            .ConfigureAwait(false);
        if (task is null)
        {
            return BulkRowOutcome.Failed(ErrorCodes.NotFound, "Workflow task not found.");
        }
        if (task.Status is WorkflowTaskStatus.Completed or WorkflowTaskStatus.Cancelled)
        {
            return BulkRowOutcome.Failed(
                "ALREADY_COMPLETED",
                $"Task is in terminal status {task.Status}; reassignment refused.");
        }

        var now = _clock.UtcNow;
        var previousAssignee = task.AssignedUserId;
        task.AssignedUserId = newAssigneeId;
        // Claiming the task clears the unclaimed stamp per the R0202 invariant.
        task.UnclaimedSinceUtc = null;
        task.UpdatedAtUtc = now;
        task.UpdatedBy = caller.UserSqid;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var detailsJson = JsonSerializer.Serialize(new
        {
            previousAssigneeUserId = previousAssignee,
            newAssigneeUserId = newAssigneeId,
        });
        await _audit.RecordAsync(
            eventCode: "WORKFLOWTASK.REASSIGNED",
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

    /// <summary>
    /// JSON-deserialisable shape of the operation's parameters payload. Internal —
    /// callers post the JSON directly; this type only exists to bind it.
    /// </summary>
    /// <param name="NewAssigneeSqid">Sqid-encoded user id of the new assignee.</param>
    private sealed record Parameters(string? NewAssigneeSqid);
}
