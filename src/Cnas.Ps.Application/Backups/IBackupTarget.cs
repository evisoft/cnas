using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Backups;

/// <summary>
/// R2307 / TOR SEC 060 — pluggable destination adapter for a backup
/// payload. The orchestrator looks adapters up by <see cref="Kind"/>;
/// production swaps in S3 / Azure / disk providers behind this contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stateless interface.</b> Implementations should be safe for shared
/// (singleton) consumption — the orchestrator may concurrently invoke
/// adapters from multiple Quartz jobs (the per-job
/// <c>DisallowConcurrentExecution</c> guard only blocks the same JobKey).
/// </para>
/// <para>
/// <b>Stable failure codes.</b> Adapters return
/// <see cref="Result.Failure"/> with these well-known codes so the
/// orchestrator can translate them into Sensitive-severity audits without
/// scraping unstable English messages.
/// </para>
/// </remarks>
public interface IBackupTarget
{
    /// <summary>
    /// Stable failure code: the target is registered but the operator never
    /// supplied the runtime configuration (endpoint / credentials).
    /// </summary>
    public const string TargetNotConfiguredCode = "BACKUP.TARGET_NOT_CONFIGURED";

    /// <summary>Stable failure code: a stored payload was requested by key but not found on the target.</summary>
    public const string StorageKeyNotFoundCode = "BACKUP.STORAGE_KEY_NOT_FOUND";

    /// <summary>Stable failure code: the upload completed but the target reported a hash mismatch.</summary>
    public const string TargetHashMismatchCode = "BACKUP.TARGET_HASH_MISMATCH";

    /// <summary>Kind this adapter handles.</summary>
    BackupTargetKind Kind { get; }

    /// <summary>
    /// Uploads <paramref name="payload"/> to the destination configured by
    /// <paramref name="policy"/>. Returns the opaque storage key the
    /// orchestrator persists on the <c>BackupRun</c> row.
    /// </summary>
    /// <param name="policy">Policy whose payload is being uploaded.</param>
    /// <param name="payload">Payload produced by an <c>IBackupPayloadProvider</c>.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Upload result on success; a deterministic failure otherwise.</returns>
    Task<Result<BackupUploadResult>> UploadAsync(
        BackupPolicy policy,
        BackupPayloadStream payload,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-downloads the payload at <paramref name="storageKey"/> for the
    /// integrity-recheck and (future) restore flows.
    /// </summary>
    /// <param name="storageKey">Key returned by an earlier <see cref="UploadAsync"/> call.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The stored payload + hash, or a deterministic failure.</returns>
    Task<Result<BackupPayloadStream>> DownloadAsync(
        string storageKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the payload at <paramref name="storageKey"/>. Called by the
    /// retention-sweep job once the run's retention window expires.
    /// </summary>
    /// <param name="storageKey">Key returned by an earlier <see cref="UploadAsync"/> call.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Success when the key is removed (or already absent), failure otherwise.</returns>
    Task<Result> DeleteAsync(
        string storageKey,
        CancellationToken cancellationToken = default);
}
