using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Reports;

/// <summary>
/// R0583 / TOR CF 09.06 / CF 09.09 — drain-half of the background report
/// runner. The Quartz <c>ReportJobBackgroundJob</c> fires every minute and
/// calls <see cref="RunBatchAsync"/> to drain up to N queued jobs; tests and
/// admin tools may invoke <see cref="RunNextAsync"/> directly to step one job
/// at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pickup discipline.</b> Both methods select the OLDEST <c>Queued</c> row
/// (ordered by <c>QueuedAtUtc</c> ascending), flip it to <c>Running</c>,
/// invoke <see cref="IReportEngine.ExportAsync"/>, persist the bytes via the
/// R0227 attachment service, and stamp a terminal status. On engine failure
/// the row flips to <c>Failed</c> with the failure message captured in
/// <c>FailureReason</c>.
/// </para>
/// <para>
/// <b>Notification.</b> Every terminal transition triggers
/// <c>INotificationService.EnqueueAsync</c> against the requester with a
/// <c>"Report.Ready"</c> / <c>"Report.Failed"</c> subject so the citizen
/// inbox + MNotify mirror receive the completion signal.
/// </para>
/// </remarks>
public interface IReportJobRunner
{
    /// <summary>
    /// Picks the oldest queued job and runs it to completion. Returns a
    /// success carrying the post-state DTO of the picked job, or a success
    /// carrying <c>null</c> when no queued jobs exist. Never throws on the
    /// empty-queue path.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// On success the post-state DTO of the picked job; the contained value is
    /// <c>null</c> when the queue was empty. On unrecoverable infrastructure
    /// failure (e.g. attachment-service error) a Result carrying a stable code.
    /// </returns>
    Task<Result<ReportJobDto?>> RunNextAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Drains up to <paramref name="maxJobs"/> queued jobs sequentially. Calls
    /// <see cref="RunNextAsync"/> in a loop and stops as soon as the queue is
    /// empty (or <paramref name="cancellationToken"/> is signalled).
    /// </summary>
    /// <param name="maxJobs">Hard cap on the number of jobs to drain in this call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of jobs actually drained.</returns>
    Task<int> RunBatchAsync(int maxJobs, CancellationToken cancellationToken = default);
}
