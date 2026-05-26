using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.DataClassification;

/// <summary>
/// R2279 / TOR SEC 033 — service façade over the data-classification catalog
/// registry. Hosts the manual-capture entrypoint, per-snapshot lookups, the
/// drift-detection path, and the finding acknowledgement path. The weekly
/// Quartz job invokes <see cref="CaptureScheduledSnapshotAsync"/> at off-peak
/// hours and follows up with an idempotent drift computation against the
/// most-recent prior <c>Captured</c> snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit attribution.</b> Each interesting transition emits a Critical
/// audit row:
/// <list type="bullet">
///   <item><see cref="CaptureManualSnapshotAsync"/> → <c>CLASSIFICATION.SNAPSHOT_CAPTURED</c> (subkind=Manual).</item>
///   <item><see cref="CaptureScheduledSnapshotAsync"/> → <c>CLASSIFICATION.SNAPSHOT_CAPTURED</c> (subkind=Scheduled).</item>
///   <item><see cref="ComputeDriftAsync"/> → <c>CLASSIFICATION.DRIFT_DETECTED</c> once per (baseline, current) pair when at least one drift was found.</item>
///   <item><see cref="AcknowledgeDriftAsync"/> → <c>CLASSIFICATION.DRIFT_ACKNOWLEDGED</c>.</item>
/// </list>
/// </para>
/// </remarks>
public interface IClassificationCatalogService
{
    /// <summary>
    /// Captures a manual snapshot — runs the scanner, persists snapshot +
    /// entries, and emits a Critical audit row.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The freshly persisted snapshot on success.</returns>
    Task<Result<ClassificationCatalogSnapshotDto>> CaptureManualSnapshotAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures a scheduled snapshot — same shape as
    /// <see cref="CaptureManualSnapshotAsync"/> but stamps
    /// <c>TriggerKind=Scheduled</c> and attributes the audit to
    /// <c>"system"</c>.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The freshly persisted snapshot on success.</returns>
    Task<Result<ClassificationCatalogSnapshotDto>> CaptureScheduledSnapshotAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Fetches a single snapshot by its Sqid (no entries attached).</summary>
    /// <param name="sqid">Sqid-encoded snapshot id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The snapshot summary on success.</returns>
    Task<Result<ClassificationCatalogSnapshotDto>> GetSnapshotByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a single snapshot together with a paged + filtered list of its
    /// per-property entries.
    /// </summary>
    /// <param name="sqid">Sqid-encoded snapshot id.</param>
    /// <param name="filter">Filter envelope (label / explicit / type-substring / page).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The snapshot details envelope on success.</returns>
    Task<Result<ClassificationCatalogSnapshotDetailsDto>> GetSnapshotDetailsAsync(
        string sqid,
        ClassificationCatalogEntryFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the most-recent snapshots, ordered by <c>CapturedAt DESC</c>.
    /// </summary>
    /// <param name="take">Number of snapshots to return (1..100).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>An ordered page on success.</returns>
    Task<Result<ClassificationCatalogSnapshotPageDto>> ListSnapshotsAsync(
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes drift between two snapshots, persisting one
    /// <c>ClassificationDriftFinding</c> row per detection. Idempotent: when
    /// findings already exist for the same (baseline, current) pair the
    /// method returns the existing rows without re-inserting and without
    /// re-auditing.
    /// </summary>
    /// <param name="baselineSnapshotSqid">Sqid of the earlier snapshot.</param>
    /// <param name="currentSnapshotSqid">Sqid of the later snapshot.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The drift result envelope on success.</returns>
    Task<Result<ClassificationDriftResultDto>> ComputeDriftAsync(
        string baselineSnapshotSqid,
        string currentSnapshotSqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists drift findings filtered by drift-kind / acknowledgement state.
    /// </summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The paged response on success.</returns>
    Task<Result<ClassificationDriftPageDto>> ListDriftFindingsAsync(
        ClassificationDriftFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acknowledges a drift finding. Stamps the acknowledging user + note + UTC
    /// timestamp on the row and emits the
    /// <c>CLASSIFICATION.DRIFT_ACKNOWLEDGED</c> Critical audit row.
    /// </summary>
    /// <param name="findingSqid">Sqid-encoded finding id.</param>
    /// <param name="input">Acknowledgement payload (note 3..1000 chars).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<ClassificationDriftFindingDto>> AcknowledgeDriftAsync(
        string findingSqid,
        ClassificationDriftAcknowledgeInputDto input,
        CancellationToken cancellationToken = default);
}
