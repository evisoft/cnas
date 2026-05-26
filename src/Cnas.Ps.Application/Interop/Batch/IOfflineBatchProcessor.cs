using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — processes one <c>Queued</c> submission to
/// completion. The Quartz job <c>OfflineBatchProcessingJob</c> picks the
/// oldest <c>Queued</c> submission per fire and delegates to this service.
/// </summary>
/// <remarks>
/// <para>
/// <b>Step-by-step.</b>
/// <list type="number">
///   <item><description>Loads the submission; rejects when <c>Status</c> != <c>Queued</c>.</description></item>
///   <item><description>Transitions to <c>Running</c> and stamps <c>StartedAt</c>.</description></item>
///   <item><description>Iterates rows in ordinal order; for each row parses
///     the JSON payload back into the op's input DTO, calls the synchronous
///     <c>IInteropApi</c> method, and projects the result back into JSON.</description></item>
///   <item><description>Builds the response CSV; persists it via
///     <see cref="IOfflineBatchBlobStore"/>; computes SHA-256 + HMAC
///     signature.</description></item>
///   <item><description>Transitions the submission to <c>Completed</c>.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Failure handling.</b> Unhandled exceptions inside the per-row dispatch
/// or the CSV-build phase flip the submission to <c>Failed</c> with a
/// sanitised <c>FailureReason</c>. A Critical
/// <c>BATCH.PROCESSING_FAILED</c> audit row is emitted.
/// </para>
/// </remarks>
public interface IOfflineBatchProcessor
{
    /// <summary>
    /// Runs the submission to completion.
    /// </summary>
    /// <param name="submissionSqid">Sqid-encoded submission id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Finalised outbound projection on success.</returns>
    Task<Result<OfflineBatchSubmissionDto>> ProcessAsync(
        string submissionSqid,
        CancellationToken cancellationToken = default);
}
