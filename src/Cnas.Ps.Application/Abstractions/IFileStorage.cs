using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// Abstraction over the object storage used to persist citizen uploads and CNAS-generated
/// documents. The default implementation is backed by MinIO (S3-compatible).
/// </summary>
/// <remarks>
/// <para>
/// R0137 — the interface ships an <c>immutable</c>-flavoured upload overload
/// (<see cref="PutAsync(string, Stream, string, bool, IFileImmutabilityMarker?, CancellationToken)"/>)
/// as a default interface method. When the caller passes <c>immutable: true</c> AND a
/// non-null <see cref="IFileImmutabilityMarker"/>, the freshly-stored object is stamped
/// in the application-level immutability ledger and any subsequent delete attempt is
/// refused by <see cref="IFileImmutabilityGuard"/>. This is application-level enforcement
/// that complements (but does not require) MinIO's server-side S3 Object Lock — for true
/// bucket-level immutability, configure the MinIO server with versioning + Object Lock
/// enabled on the relevant buckets.
/// </para>
/// </remarks>
public interface IFileStorage
{
    /// <summary>
    /// Stores the supplied content under a randomly-generated object key in <paramref name="bucket"/>.
    /// Returns the generated key on success.
    /// </summary>
    /// <param name="bucket">Bucket name (citizen uploads vs generated documents).</param>
    /// <param name="content">Open, readable stream positioned at byte 0.</param>
    /// <param name="contentType">Declared MIME type (validated against magic bytes by the caller).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result<StoredObject>> PutAsync(
        string bucket,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// R0137 — extended upload that records an application-level immutability stamp
    /// when <paramref name="immutable"/> is true and a non-null
    /// <paramref name="marker"/> is supplied. Defaults preserve back-compat for every
    /// existing call site — passing no extra arguments behaves identically to
    /// <see cref="PutAsync(string, Stream, string, CancellationToken)"/>.
    /// </summary>
    /// <param name="bucket">Bucket name.</param>
    /// <param name="content">Open, readable stream positioned at byte 0.</param>
    /// <param name="contentType">Declared MIME type.</param>
    /// <param name="immutable">
    /// When <c>true</c> AND <paramref name="marker"/> is non-null, the freshly-stored
    /// object key is recorded in the application-level immutability ledger; subsequent
    /// delete attempts via paths protected by <see cref="IFileImmutabilityGuard"/> will
    /// be refused with the stable <see cref="ErrorCodes.ImmutableObject"/> code.
    /// </param>
    /// <param name="marker">
    /// Marker service used to stamp the object as immutable. May be <c>null</c> when
    /// the caller does not need immutability — in which case <paramref name="immutable"/>
    /// is treated as <c>false</c> regardless of its value.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The <see cref="StoredObject"/> descriptor, or a failure result.</returns>
    async Task<Result<StoredObject>> PutAsync(
        string bucket,
        Stream content,
        string contentType,
        bool immutable,
        IFileImmutabilityMarker? marker,
        CancellationToken cancellationToken = default)
    {
        var stored = await PutAsync(bucket, content, contentType, cancellationToken)
            .ConfigureAwait(false);
        if (stored.IsFailure || !immutable || marker is null)
        {
            return stored;
        }

        // Best-effort: a marker failure does NOT roll back the upload. The upload is
        // already durable in object storage; a follow-up administrative mark can be
        // applied if the first attempt lost. The mark failure is propagated so the
        // caller (and the audit log) records the partial-success.
        var marked = await marker
            .MarkImmutableAsync(bucket, stored.Value!.ObjectKey, reason: null, cancellationToken)
            .ConfigureAwait(false);
        if (marked.IsFailure)
        {
            return Result<StoredObject>.Failure(
                marked.ErrorCode!,
                marked.ErrorMessage ?? "Immutability mark failed.");
        }
        return stored;
    }

    /// <summary>Streams the object identified by <paramref name="objectKey"/> back to the caller.</summary>
    Task<Result<Stream>> GetAsync(
        string bucket,
        string objectKey,
        CancellationToken cancellationToken = default);

    /// <summary>Builds a time-limited presigned URL the caller can hand to a browser for direct download.</summary>
    Task<Result<Uri>> PresignDownloadAsync(
        string bucket,
        string objectKey,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the object from storage. Idempotent — missing objects are not an error.
    /// </summary>
    /// <remarks>
    /// R0137 — this method DOES NOT consult <see cref="IFileImmutabilityGuard"/>. Callers
    /// who must honour the immutability ledger MUST invoke
    /// <see cref="IFileImmutabilityGuard.CheckBeforeDeleteAsync"/> before calling this
    /// method and propagate the failure when the guard refuses. The guard is intentionally
    /// kept as a separate composition step so internal one-off cleanups (e.g. failed-upload
    /// rollback inside a transaction) can bypass it deliberately when the row never
    /// reached the immutability table in the first place.
    /// </remarks>
    Task<Result> DeleteAsync(
        string bucket,
        string objectKey,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Descriptor returned after a successful <see cref="IFileStorage.PutAsync(string, System.IO.Stream, string, System.Threading.CancellationToken)"/>.
/// Carries the random object key + SHA-256 digest so callers can persist them on the
/// corresponding <c>Document</c> entity.
/// </summary>
/// <param name="ObjectKey">Random object key under which the content was stored.</param>
/// <param name="ContentSha256Hex">SHA-256 hex digest of the content, for integrity verification.</param>
/// <param name="SizeBytes">Size in bytes.</param>
public sealed record StoredObject(string ObjectKey, string ContentSha256Hex, long SizeBytes);
