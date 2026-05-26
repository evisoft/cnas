namespace Cnas.Ps.Application.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — minimal byte-blob storage abstraction used by the
/// offline-batch subsystem. The default implementation
/// (<c>InMemoryOfflineBatchBlobStore</c>) is dictionary-backed; production
/// swaps it for an S3 / MinIO adapter (deferred to the deployment-prep
/// iteration).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated abstraction.</b> The existing
/// <c>IBlobStorage</c> attachment adapter is bound to the attachment-table
/// lifecycle (filename + content-type derived from
/// <c>AttachmentValidator</c>). The batch subsystem stores request /
/// response CSVs without those attachment-side hooks — keeping the
/// concerns separated makes both contracts simpler.
/// </para>
/// </remarks>
public interface IOfflineBatchBlobStore
{
    /// <summary>
    /// Persists the supplied payload and returns an opaque storage key the
    /// service can use to fetch it back later.
    /// </summary>
    /// <param name="payload">Raw bytes to persist.</param>
    /// <param name="contentType">MIME content type (e.g. <c>text/csv</c>).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Opaque storage key.</returns>
    Task<string> PutAsync(
        byte[] payload,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the bytes previously stored under the supplied key.
    /// </summary>
    /// <param name="storageKey">Opaque storage key returned by <see cref="PutAsync"/>.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The persisted bytes.</returns>
    /// <exception cref="System.IO.FileNotFoundException">When the key is unknown.</exception>
    Task<byte[]> GetAsync(string storageKey, CancellationToken cancellationToken = default);
}
