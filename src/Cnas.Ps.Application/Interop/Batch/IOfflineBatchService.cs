using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Interop.Batch;

/// <summary>
/// R2161 / TOR INT 002 — generic CnasUser-facing offline-batch façade. Wraps
/// the lightweight ingest / export submission flow that sits alongside the
/// real-time REST surface. Separate from the Annex-4 / INT 002 B2B file-based
/// registry (<see cref="IOfflineBatchSubmissionService"/>) — that surface is
/// keyed by consumer-subject + Annex-4 op code; this one is the generic
/// user-facing fallback for ad-hoc ingest / export jobs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Workflow.</b>
/// <list type="number">
///   <item><description>Caller submits a multi-record payload through one of the two
///     <c>Submit*</c> methods. The service validates the row count (≤ 10 000) and
///     persists an <see cref="Cnas.Ps.Core.Domain.OfflineBatchJob"/> row in
///     <see cref="Cnas.Ps.Core.Domain.OfflineBatchJobStatus.Pending"/>.</description></item>
///   <item><description>A one-shot Quartz trigger picks the row up and runs
///     <c>OfflineBatchProcessor</c> in Infrastructure/Jobs. The processor
///     transitions through Running → Completed (or Failed).</description></item>
///   <item><description>Caller polls <see cref="GetStatusAsync"/> to follow the
///     job's progress.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Audit.</b> Every state-changing call emits a Critical
/// <c>OFFLINE_BATCH.SUBMITTED</c> audit row carrying the row count, kind, and
/// caller id. The row count is informational only — the actual payload is
/// held on the processor side and is never echoed onto an audit row.
/// </para>
/// </remarks>
public interface IOfflineBatchService
{
    /// <summary>Stable audit code emitted on successful ingest submission.</summary>
    public const string AuditIngestSubmitted = "OFFLINE_BATCH.INGEST_SUBMITTED";

    /// <summary>Stable audit code emitted on successful export submission.</summary>
    public const string AuditExportSubmitted = "OFFLINE_BATCH.EXPORT_SUBMITTED";

    /// <summary>Stable failure code returned when the payload exceeds the 10 000-row cap.</summary>
    public const string PayloadTooLargeCode = "OFFLINE_BATCH.PAYLOAD_TOO_LARGE";

    /// <summary>Maximum row count allowed per submission.</summary>
    public const int MaxRows = 10_000;

    /// <summary>
    /// Persists a <see cref="Cnas.Ps.Core.Domain.OfflineBatchJobKind.Ingest"/>
    /// job row in <see cref="Cnas.Ps.Core.Domain.OfflineBatchJobStatus.Pending"/>
    /// and schedules a one-shot processor fire.
    /// </summary>
    /// <param name="input">Submission input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="OfflineBatchJobDto"/> projection of the persisted row, or
    /// <see cref="PayloadTooLargeCode"/> when the row count exceeds the cap.
    /// </returns>
    Task<Result<OfflineBatchJobDto>> SubmitIngestAsync(
        OfflineBatchIngestInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a <see cref="Cnas.Ps.Core.Domain.OfflineBatchJobKind.Export"/>
    /// job row in <see cref="Cnas.Ps.Core.Domain.OfflineBatchJobStatus.Pending"/>
    /// and schedules a one-shot processor fire.
    /// </summary>
    /// <param name="input">Submission input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="OfflineBatchJobDto"/> projection of the persisted row, or
    /// <see cref="PayloadTooLargeCode"/> when the filter count exceeds the cap.
    /// </returns>
    Task<Result<OfflineBatchJobDto>> SubmitExportAsync(
        OfflineBatchExportInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current state of the supplied job. The lookup is scoped
    /// to the caller — cross-user reads surface as <see cref="Cnas.Ps.Core.Common.ErrorCodes.NotFound"/>
    /// so an unrelated job's existence never leaks.
    /// </summary>
    /// <param name="sqid">Sqid-encoded job id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The job projection on success.</returns>
    Task<Result<OfflineBatchJobDto>> GetStatusAsync(
        string sqid,
        CancellationToken cancellationToken = default);
}
