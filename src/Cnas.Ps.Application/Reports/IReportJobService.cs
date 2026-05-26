using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Reports;

/// <summary>
/// R0583 / TOR CF 09.06 / CF 09.09 — façade for the user-driven half of the
/// background report runner. Callers enqueue executions of an existing
/// <c>ReportTemplate</c> via <see cref="EnqueueAsync"/>; the
/// <see cref="IReportJobRunner"/> background runner picks up the queued rows,
/// runs the engine, persists the bytes through the R0227 attachment
/// subsystem, and notifies the user via the R0171/R0128 orchestrator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stable error codes.</b>
/// <see cref="ErrorCodes.NotFound"/> — the template (or job) does not exist.
/// <see cref="ErrorCodes.Forbidden"/> — the caller is not the requester.
/// <see cref="ErrorCodes.Unauthorized"/> — the caller is anonymous.
/// <see cref="ErrorCodes.InvalidSqid"/> — the supplied Sqid does not decode.
/// <see cref="ErrorCodes.ValidationFailed"/> — wire-shape validation failed
/// (e.g. format string unparseable, or transition guard refused).
/// </para>
/// <para>
/// <b>Sqid invariant.</b> Every id surface is the Sqid string form per
/// CLAUDE.md RULE 3. Raw <see cref="long"/> primary keys never appear here.
/// </para>
/// </remarks>
public interface IReportJobService
{
    /// <summary>
    /// Enqueues a new background execution of the supplied report template.
    /// The job lands in <c>Queued</c> status; the background runner picks it
    /// up on its next tick.
    /// </summary>
    /// <param name="input">Enqueue payload (Sqid template id + format).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// On success the persisted job DTO; on failure a Result carrying a stable
    /// error code from the type-level remarks.
    /// </returns>
    Task<Result<ReportJobDto>> EnqueueAsync(
        ReportJobEnqueueDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches one job by its internal id. Only the requester (or an
    /// administrator) may read the row.
    /// </summary>
    /// <param name="jobId">Internal id of the job.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The job DTO or a failure with a stable error code.</returns>
    Task<Result<ReportJobDto>> GetAsync(
        long jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the calling user's most recent job rows (newest first). Returns
    /// an empty list when the caller is anonymous.
    /// </summary>
    /// <param name="take">Maximum number of rows to return (clamped to <c>[1, 100]</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The job DTOs ordered by <c>QueuedAtUtc</c> descending.</returns>
    Task<IReadOnlyList<ReportJobDto>> ListForCurrentUserAsync(
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a <c>Queued</c> job. Cancellation is only valid while the job
    /// has not yet been picked up by the runner — a <c>Running</c> or
    /// terminal-status row returns
    /// <see cref="ErrorCodes.ValidationFailed"/> carrying the stable
    /// <c>JOB_NOT_CANCELLABLE</c> message.
    /// </summary>
    /// <param name="jobId">Internal id of the job.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success or a stable failure code.</returns>
    Task<Result> CancelAsync(long jobId, CancellationToken cancellationToken = default);
}
