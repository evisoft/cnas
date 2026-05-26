namespace Cnas.Ps.Application.Attachments;

/// <summary>
/// R0227 / TOR UI 014 — narrow byte-array blob primitive used by
/// <c>IAttachmentService</c>. Distinct from the existing <c>IFileStorage</c>
/// stream-based surface so the attachment subsystem can swap blob backends
/// (local disk for dev, MinIO for prod, future S3 / Azure blob) without dragging
/// the legacy stream-oriented document service along. The interface ONLY surfaces
/// the three operations the attachment service needs — Put / Get / Delete — and
/// works in raw <see cref="byte"/>[] because the in-process service has already
/// loaded and hashed the payload by the time it reaches the storage call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Key opacity.</b> The <see cref="string"/> key is opaque end-to-end —
/// callers MUST treat it as a black box. The local-disk adapter formats keys as
/// <c>attachments/yyyy/MM/dd/{guid}</c>; the (future) MinIO adapter may use a
/// different shape. Storing the key on the row works regardless.
/// </para>
/// <para>
/// <b>Path-traversal guard.</b> Every implementation MUST reject keys that
/// resolve outside its configured root (e.g. via <c>..</c> segments). The
/// in-process local-disk adapter performs this check explicitly.
/// </para>
/// <para>
/// <b>Idempotent delete.</b> <see cref="DeleteAsync"/> is idempotent — deleting
/// a non-existent key is a successful no-op so the service-layer cascade after a
/// soft-archive does not fail when the backend has already pruned the object.
/// </para>
/// </remarks>
public interface IBlobStorage
{
    /// <summary>
    /// Persists <paramref name="bytes"/> under the supplied <paramref name="key"/>.
    /// Implementations create parent directories / buckets as needed and must
    /// reject keys that escape their configured root.
    /// </summary>
    /// <param name="key">Opaque storage key chosen by the caller (e.g. the service).</param>
    /// <param name="bytes">Byte payload to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PutAsync(string key, byte[] bytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads back the bytes previously persisted under <paramref name="key"/>. Throws
    /// <see cref="FileNotFoundException"/> when the key is unknown.
    /// </summary>
    /// <param name="key">Opaque storage key previously returned by <see cref="PutAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw bytes.</returns>
    Task<byte[]> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the object identified by <paramref name="key"/>. Idempotent — deleting
    /// a missing key succeeds silently.
    /// </summary>
    /// <param name="key">Opaque storage key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
