using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — bundles the response-file bytes + the download
/// descriptor in a single tuple returned by
/// <c>IOfflineBatchSubmissionService.GetDownloadBytesAsync</c>.
/// </summary>
/// <param name="Info">Download descriptor (filename / hash / signature).</param>
/// <param name="Bytes">Raw bytes of the response CSV.</param>
public sealed record OfflineBatchDownloadBytesDto(OfflineBatchDownloadInfoDto Info, byte[] Bytes);

/// <summary>
/// R1710 / TOR INT 002 — consumer + admin façade over the offline-batch
/// registry. Handles submission, cancellation, lookup, download-info, and
/// list queries. The synchronous Annex-4 endpoint set (R0634 + R1702-R1708)
/// covers the real-time path; this service covers the file-based, queued
/// path used for nightly reconciliations and large back-fills.
/// </summary>
public interface IOfflineBatchSubmissionService
{
    /// <summary>
    /// Persists the request CSV, parses it into rows, transitions the
    /// submission through <c>Submitted → Queued</c>, and emits the
    /// <c>BATCH.SUBMITTED</c> audit row + the submitted metric.
    /// </summary>
    /// <param name="input">Submission input (consumer subject already filled by the controller).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Outbound projection on success.</returns>
    Task<Result<OfflineBatchSubmissionDto>> SubmitAsync(
        OfflineBatchSubmissionInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a <c>Submitted</c> or <c>Queued</c> submission. Running
    /// submissions cannot be cancelled — the processor is mid-iteration and
    /// the response file is being streamed to blob storage.
    /// </summary>
    /// <param name="sqid">Sqid-encoded submission id.</param>
    /// <param name="input">Cancellation rationale (3..500 characters).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Updated outbound projection on success.</returns>
    Task<Result<OfflineBatchSubmissionDto>> CancelAsync(
        string sqid,
        OfflineBatchReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a single submission by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded submission id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Outbound projection on success.</returns>
    Task<Result<OfflineBatchSubmissionDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the download descriptor for a <c>Completed</c> submission.
    /// Other lifecycle states surface as <c>Conflict</c>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded submission id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Download descriptor on success.</returns>
    Task<Result<OfflineBatchDownloadInfoDto>> GetDownloadInfoAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the bytes of the response CSV for a Completed submission.
    /// Used by the controller to stream the download to the consumer. The
    /// service treats this as a privileged operation and emits the
    /// <c>BATCH.DOWNLOADED</c> audit row.
    /// </summary>
    /// <param name="sqid">Sqid-encoded submission id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuple of bytes + download descriptor on success.</returns>
    Task<Result<OfflineBatchDownloadBytesDto>> GetDownloadBytesAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>Lists submissions matching the supplied filter.</summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Paged envelope on success.</returns>
    Task<Result<OfflineBatchSubmissionPageDto>> ListAsync(
        OfflineBatchSubmissionFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>Lists rows inside a single submission.</summary>
    /// <param name="sqid">Sqid-encoded submission id.</param>
    /// <param name="filter">Row-list filter envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Paged row envelope on success.</returns>
    Task<Result<OfflineBatchRowPageDto>> ListRowsAsync(
        string sqid,
        OfflineBatchRowFilterDto filter,
        CancellationToken cancellationToken = default);
}
