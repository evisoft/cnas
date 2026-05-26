using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.BulkActions;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — orchestrator that consumes a
/// <see cref="IBulkSelectionService"/> handle and executes the registered
/// <see cref="IBulkOperation"/> against the resolved row set. One call to
/// <see cref="RunAsync"/> produces exactly one <c>BulkOperationRun</c> row.
/// </summary>
/// <remarks>
/// <para>
/// <b>High-level steps.</b>
/// <list type="number">
///   <item><description>Look up an existing run for the (Actor, Code, IdempotencyKey)
///   triple — return verbatim if present (the operation is NOT re-executed).</description></item>
///   <item><description>Load the selection. Reject if expired, already consumed, or
///   owned by a different user.</description></item>
///   <item><description>Verify the operation exists in the registry and its registry
///   matches the selection's registry.</description></item>
///   <item><description>Resolve the live id list via <see cref="IBulkSelectionService.ResolveIdsAsync"/>.</description></item>
///   <item><description>Enforce <c>op.MaxRowsPerRun</c> — refuse with
///   <see cref="ErrorCodes.BulkQuotaExceeded"/> on overflow.</description></item>
///   <item><description>Persist a <c>BulkOperationRun</c> row in
///   <c>BulkOperationStatus.Running</c> so partial progress survives a crash.</description></item>
///   <item><description>Emit a <c>BULK.{Code}.STARTED</c> audit row (severity
///   Critical — mirrored to MLog per TOR SEC 056).</description></item>
///   <item><description>Process each row serially via the operation; aggregate the
///   per-row outcomes into the run counters and into the
///   <c>FailureSummaryJson</c> array (capped at 100 entries).</description></item>
///   <item><description>Stamp the terminal status
///   (<c>Completed</c>|<c>PartiallyFailed</c>|<c>Failed</c>), mark the selection
///   <c>IsConsumed = true</c>, and emit a <c>BULK.{Code}.COMPLETED</c> audit row.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Why serial.</b> Each operation calls into transactional services (EF Core +
/// audit writes); parallelism would not only multiply DB contention but also break
/// the ordering guarantees the audit pipeline relies on. The serial loop is also
/// the simplest model for the per-row error capture and idempotency contract.
/// </para>
/// </remarks>
public interface IBulkOperationRunner
{
    /// <summary>
    /// Executes a registered bulk operation against the resolved row set of a
    /// previously-persisted bulk selection.
    /// </summary>
    /// <param name="bulkSelectionId">Internal primary key of the selection row.</param>
    /// <param name="operationCode">
    /// Stable operation code (e.g. <c>WorkflowTask.Reassign</c>) matching a
    /// registered <see cref="IBulkOperation.Code"/>.
    /// </param>
    /// <param name="parametersJson">
    /// Operation-specific parameters. Required when
    /// <see cref="IBulkOperation.RequiresParameters"/> is <c>true</c>; null otherwise.
    /// </param>
    /// <param name="idempotencyKey">
    /// Optional caller-supplied de-duplication key. When provided and a prior run
    /// exists for the matching <c>(ActorUserId, OperationCode, IdempotencyKey)</c>
    /// triple, the prior run is returned verbatim without executing the operation
    /// again.
    /// </param>
    /// <param name="ct">Cancellation token. Honoured by the per-row inner loop.</param>
    /// <returns>
    /// On success the <see cref="BulkOperationRunOutputDto"/> describing the final
    /// run state. Failure codes:
    /// <list type="bullet">
    ///   <item><description><see cref="ErrorCodes.Unauthorized"/> — caller anonymous.</description></item>
    ///   <item><description><see cref="ErrorCodes.NotFound"/> — selection missing.</description></item>
    ///   <item><description><see cref="ErrorCodes.Forbidden"/> — selection owned by another user.</description></item>
    ///   <item><description><see cref="ErrorCodes.BulkSelectionExpired"/> — selection past <c>ExpiresAtUtc</c>.</description></item>
    ///   <item><description><see cref="ErrorCodes.BulkSelectionConsumed"/> — selection already consumed.</description></item>
    ///   <item><description><see cref="ErrorCodes.BulkOperationUnknown"/> — operation code not in the registry.</description></item>
    ///   <item><description><see cref="ErrorCodes.BulkQuotaExceeded"/> — resolved row count above <c>op.MaxRowsPerRun</c>.</description></item>
    ///   <item><description><see cref="ErrorCodes.ValidationFailed"/> — registry mismatch, missing parameters, malformed input.</description></item>
    /// </list>
    /// </returns>
    Task<Result<BulkOperationRunOutputDto>> RunAsync(
        long bulkSelectionId,
        string operationCode,
        string? parametersJson,
        string? idempotencyKey,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches a previously-executed run by Sqid. The caller must be the actor that
    /// submitted the run — non-actors receive <see cref="ErrorCodes.Forbidden"/>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the run row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>On success the run output; otherwise a structured failure.</returns>
    Task<Result<BulkOperationRunOutputDto>> GetAsync(string sqid, CancellationToken ct = default);
}
