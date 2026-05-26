using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.BulkActions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — default <see cref="IBulkOperationRunner"/>
/// implementation. Orchestrates one execution of a registered
/// <see cref="IBulkOperation"/> against the resolved row set of a
/// <see cref="BulkSelection"/>. See the interface XML doc for the high-level steps.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-row failure tolerance.</b> Each <see cref="IBulkOperation.ExecuteAsync"/>
/// call is wrapped in a try / catch — exceptions thrown by the operation become a
/// failed <see cref="BulkRowOutcome"/> rather than aborting the loop. Counters bump
/// accordingly; the first 100 failures land in <c>FailureSummaryJson</c>; beyond the
/// cap operators rely on the audit trail.
/// </para>
/// <para>
/// <b>Audit shape.</b> The runner emits two audit rows per run:
/// <list type="bullet">
///   <item>
///     <description><c>BULK.{OperationCode}.STARTED</c> at the start with a
///     <c>{ totalRows, selectionId, runId }</c> payload — severity Critical so
///     MLog mirroring fires per TOR SEC 056.</description>
///   </item>
///   <item>
///     <description><c>BULK.{OperationCode}.COMPLETED</c> at the end with a
///     <c>{ status, total, succeeded, failed }</c> payload — also severity
///     Critical.</description>
///   </item>
/// </list>
/// Individual rows are NOT separately audited by the runner; the operation
/// implementations are expected to emit per-row audit rows themselves when they
/// matter (e.g. <c>WORKFLOWTASK.REASSIGNED</c>).
/// </para>
/// </remarks>
public sealed class BulkOperationRunner : IBulkOperationRunner
{
    private readonly ICnasDbContext _db;
    private readonly ICallerContext _caller;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly IBulkSelectionService _selections;
    private readonly IBulkOperationRegistry _registry;
    private readonly IAuditService _audit;
    private readonly BulkOperationOptions _opts;
    private readonly ILogger<BulkOperationRunner> _logger;

    /// <summary>Creates the runner.</summary>
    /// <param name="db">Per-request DbContext.</param>
    /// <param name="caller">Per-request caller context.</param>
    /// <param name="sqids">Sqid encoder used for external ids on the run output.</param>
    /// <param name="clock">UTC clock used to stamp timestamps.</param>
    /// <param name="selections">Bulk-selection service used to resolve the live id list at run time.</param>
    /// <param name="registry">Operation registry used to dispatch on the caller-supplied code.</param>
    /// <param name="audit">Audit service consumed by the start/end audit rows.</param>
    /// <param name="options">Bound bulk-operation options (global quota, failure cap).</param>
    /// <param name="logger">Structured logger.</param>
    public BulkOperationRunner(
        ICnasDbContext db,
        ICallerContext caller,
        ISqidService sqids,
        ICnasTimeProvider clock,
        IBulkSelectionService selections,
        IBulkOperationRegistry registry,
        IAuditService audit,
        IOptions<BulkOperationOptions> options,
        ILogger<BulkOperationRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _caller = caller;
        _sqids = sqids;
        _clock = clock;
        _selections = selections;
        _registry = registry;
        _audit = audit;
        _opts = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<BulkOperationRunOutputDto>> RunAsync(
        long bulkSelectionId,
        string operationCode,
        string? parametersJson,
        string? idempotencyKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationCode);

        if (_caller.UserId is not long actorId)
        {
            return Result<BulkOperationRunOutputDto>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        // ── 1. Idempotency lookup. If the caller supplied a key and a prior run
        // exists for the same (actor, code, key) triple, return that run verbatim.
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var prior = await _db.BulkOperationRuns
                .SingleOrDefaultAsync(
                    r => r.ActorUserId == actorId
                      && r.OperationCode == operationCode
                      && r.IdempotencyKey == idempotencyKey,
                    ct).ConfigureAwait(false);
            if (prior is not null)
            {
                return Result<BulkOperationRunOutputDto>.Success(Project(prior));
            }
        }

        // ── 2. Look up the operation in the registry.
        if (!_registry.TryGet(operationCode, out var op))
        {
            return Result<BulkOperationRunOutputDto>.Failure(
                ErrorCodes.BulkOperationUnknown,
                $"No bulk operation registered for code '{operationCode}'.");
        }

        if (op.RequiresParameters && string.IsNullOrWhiteSpace(parametersJson))
        {
            return Result<BulkOperationRunOutputDto>.Failure(
                ErrorCodes.ValidationFailed,
                $"Operation '{operationCode}' requires ParametersJson.");
        }

        // ── 3. Load the selection. Ownership / expiry / consumed checks all happen
        // here so the runner never enters the per-row loop on a degenerate selection.
        var selection = await _db.BulkSelections
            .SingleOrDefaultAsync(s => s.Id == bulkSelectionId && s.IsActive, ct)
            .ConfigureAwait(false);
        if (selection is null)
        {
            return Result<BulkOperationRunOutputDto>.Failure(
                ErrorCodes.NotFound, "Bulk selection not found.");
        }
        if (selection.OwnerUserId != actorId)
        {
            return Result<BulkOperationRunOutputDto>.Failure(
                ErrorCodes.Forbidden, "Bulk selection is owned by another user.");
        }
        if (selection.IsConsumed)
        {
            return Result<BulkOperationRunOutputDto>.Failure(
                ErrorCodes.BulkSelectionConsumed,
                "Bulk selection has already been consumed by a prior run.");
        }
        if (selection.ExpiresAtUtc <= _clock.UtcNow)
        {
            return Result<BulkOperationRunOutputDto>.Failure(
                ErrorCodes.BulkSelectionExpired, "Bulk selection has expired.");
        }
        if (!string.Equals(selection.Registry, op.Registry, StringComparison.Ordinal))
        {
            return Result<BulkOperationRunOutputDto>.Failure(
                ErrorCodes.ValidationFailed,
                $"Operation registry '{op.Registry}' does not match selection registry '{selection.Registry}'.");
        }

        // ── 4. Resolve the live id list (TOCTOU-safe — runs after the selection
        // load so any change between the two is bounded by transaction isolation).
        var resolved = await _selections.ResolveIdsAsync(selection.Id, ct).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return Result<BulkOperationRunOutputDto>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
        }

        var rowIds = resolved.Value;

        // ── 5. Quota check. The per-op cap wins; a sentinel 0 (operation declared
        // "use the global cap") would defer to the global options floor.
        var cap = op.MaxRowsPerRun > 0 ? op.MaxRowsPerRun : _opts.MaxRowsPerRun;
        if (rowIds.Count > cap)
        {
            return Result<BulkOperationRunOutputDto>.Failure(
                ErrorCodes.BulkQuotaExceeded,
                $"Resolved row count {rowIds.Count} exceeds per-operation cap {cap}.");
        }

        // ── 6. Persist the run row in Running state so partial progress survives a
        // crash. SaveChanges is intentional — a separate transaction from the
        // per-row work below.
        var now = _clock.UtcNow;
        var run = new BulkOperationRun
        {
            BulkSelectionId = selection.Id,
            OperationCode = op.Code,
            ActorUserId = actorId,
            Status = BulkOperationStatus.Running,
            TotalRows = rowIds.Count,
            SucceededRows = 0,
            FailedRows = 0,
            StartedUtc = now,
            ParametersJson = parametersJson,
            IdempotencyKey = idempotencyKey,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.BulkOperationRuns.Add(run);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // ── 7. Start audit. Severity Critical so MLog mirroring fires.
        await EmitAuditAsync(
            $"BULK.{op.Code}.STARTED",
            new
            {
                runId = _sqids.Encode(run.Id),
                selectionId = _sqids.Encode(selection.Id),
                totalRows = rowIds.Count,
                registry = selection.Registry,
            },
            run.Id,
            ct).ConfigureAwait(false);

        // ── 8. Per-row loop. Serial — operations call into transactional services
        // that don't expect concurrent invocation; ordering matters for the audit
        // trail; the per-row failure capture is simpler without parallel state.
        var failures = new List<object>(capacity: Math.Min(_opts.MaxFailureSummaryEntries, rowIds.Count));
        foreach (var rowId in rowIds)
        {
            if (ct.IsCancellationRequested)
            {
                run.Status = BulkOperationStatus.Cancelled;
                break;
            }

            BulkRowOutcome outcome;
            try
            {
                outcome = await op.ExecuteAsync(rowId, parametersJson, _caller, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Bulk operation {Code} threw on row {RowId}.", op.Code, rowId);
                outcome = BulkRowOutcome.Failed(ErrorCodes.Internal, ex.Message);
            }

            if (outcome.Success)
            {
                run.SucceededRows++;
            }
            else
            {
                run.FailedRows++;
                if (failures.Count < _opts.MaxFailureSummaryEntries)
                {
                    failures.Add(new
                    {
                        rowId = _sqids.Encode(rowId),
                        errorCode = outcome.ErrorCode,
                        message = outcome.Message,
                    });
                }
            }
        }

        // ── 9. Stamp terminal status.
        if (run.Status != BulkOperationStatus.Cancelled)
        {
            run.Status = run.FailedRows == 0
                ? BulkOperationStatus.Completed
                : (run.SucceededRows == 0
                    ? BulkOperationStatus.Failed
                    : BulkOperationStatus.PartiallyFailed);
        }
        run.CompletedUtc = _clock.UtcNow;
        if (failures.Count > 0)
        {
            run.FailureSummaryJson = JsonSerializer.Serialize(failures);
        }
        selection.IsConsumed = true;
        selection.UpdatedAtUtc = run.CompletedUtc;
        selection.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // ── 10. End audit. Severity Critical (same rationale as the STARTED row).
        await EmitAuditAsync(
            $"BULK.{op.Code}.COMPLETED",
            new
            {
                runId = _sqids.Encode(run.Id),
                status = run.Status.ToString(),
                total = run.TotalRows,
                succeeded = run.SucceededRows,
                failed = run.FailedRows,
            },
            run.Id,
            ct).ConfigureAwait(false);

        return Result<BulkOperationRunOutputDto>.Success(Project(run));
    }

    /// <inheritdoc />
    public async Task<Result<BulkOperationRunOutputDto>> GetAsync(string sqid, CancellationToken ct = default)
    {
        if (_caller.UserId is not long callerId)
        {
            return Result<BulkOperationRunOutputDto>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<BulkOperationRunOutputDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var run = await _db.BulkOperationRuns
            .SingleOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, ct)
            .ConfigureAwait(false);
        if (run is null)
        {
            return Result<BulkOperationRunOutputDto>.Failure(
                ErrorCodes.NotFound, "Bulk operation run not found.");
        }
        if (run.ActorUserId != callerId)
        {
            return Result<BulkOperationRunOutputDto>.Failure(
                ErrorCodes.Forbidden, "Bulk operation run belongs to another user.");
        }
        return Result<BulkOperationRunOutputDto>.Success(Project(run));
    }

    /// <summary>Projects an entity row to the public DTO with Sqid-encoded id and stable-string status.</summary>
    /// <param name="row">Loaded run entity.</param>
    /// <returns>The DTO the API surfaces.</returns>
    private BulkOperationRunOutputDto Project(BulkOperationRun row) => new(
        _sqids.Encode(row.Id),
        row.OperationCode,
        row.Status.ToString(),
        row.TotalRows,
        row.SucceededRows,
        row.FailedRows,
        row.StartedUtc,
        row.CompletedUtc,
        row.FailureSummaryJson,
        row.IdempotencyKey);

    /// <summary>Centralised audit-emit helper used for the start/end markers.</summary>
    /// <param name="eventCode">Stable event code (e.g. <c>BULK.WorkflowTask.Reassign.STARTED</c>).</param>
    /// <param name="payload">Anonymous object serialised to <c>DetailsJson</c>.</param>
    /// <param name="targetEntityId">Internal run id so investigators can join back.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task EmitAuditAsync(string eventCode, object payload, long targetEntityId, CancellationToken ct)
    {
        var detailsJson = JsonSerializer.Serialize(payload);
        var actor = _caller.UserSqid ?? "system";
        await _audit.RecordAsync(
            eventCode: eventCode,
            severity: AuditSeverity.Critical,
            actorId: actor,
            targetEntity: nameof(BulkOperationRun),
            targetEntityId: targetEntityId,
            detailsJson: detailsJson,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);
    }
}
