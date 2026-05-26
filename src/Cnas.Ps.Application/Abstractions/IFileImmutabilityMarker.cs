using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// R0137 — application-level facade for stamping an object-storage object as immutable.
/// Each successful <see cref="MarkImmutableAsync"/> persists a
/// <c>Cnas.Ps.Core.Domain.FileImmutabilityRecord</c> row keyed by <c>(bucket, objectKey)</c>
/// — subsequent calls to <see cref="IFileImmutabilityGuard.CheckBeforeDeleteAsync"/>
/// for the same key fail with <c>FILESTORAGE.IMMUTABLE_OBJECT</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an application-level marker.</b> True bucket-level immutability is a MinIO
/// server-side feature (S3 Object Lock) that requires versioned buckets plus a
/// deployment-time toggle. Until that infrastructure choice is finalised across every
/// environment, the application records its own immutability ledger so production
/// callers still get a deterministic "this object MUST NOT be deleted" semantic
/// regardless of the underlying bucket configuration. When the server-side lock is
/// finally enabled the application-level marker becomes belt-and-braces; both layers
/// refuse a delete and the audit log records the rejection consistently.
/// </para>
/// <para>
/// <b>Idempotency.</b> A second mark for the same <c>(bucket, objectKey)</c> pair
/// short-circuits to a no-op success — the partial unique index on
/// <c>(Bucket, ObjectKey) WHERE IsActive=true</c> backs the deterministic detection.
/// Callers therefore do not need to guard against double-marks themselves.
/// </para>
/// </remarks>
public interface IFileImmutabilityMarker
{
    /// <summary>
    /// Stamps the supplied <c>(bucket, objectKey)</c> as immutable. Subsequent deletes
    /// via paths protected by <see cref="IFileImmutabilityGuard"/> will be refused.
    /// </summary>
    /// <param name="bucket">Storage bucket name. Must be non-empty.</param>
    /// <param name="objectKey">Object key within the bucket. Must be non-empty.</param>
    /// <param name="reason">Optional free-form rationale captured on the record for forensic debugging.</param>
    /// <param name="cancellationToken">Cancellation token plumbed through to <c>SaveChangesAsync</c>.</param>
    /// <returns>
    /// <c>Result.Success</c> on first mark or idempotent re-mark.
    /// <c>Result.Failure(ErrorCodes.ValidationFailed)</c> when either input is blank.
    /// </returns>
    Task<Result> MarkImmutableAsync(
        string bucket,
        string objectKey,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> when the supplied <c>(bucket, objectKey)</c> has been stamped
    /// as immutable (and that stamp has not been soft-deleted).
    /// </summary>
    /// <param name="bucket">Storage bucket name.</param>
    /// <param name="objectKey">Object key within the bucket.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when an active immutability record exists; <c>false</c> otherwise.</returns>
    Task<bool> IsImmutableAsync(
        string bucket,
        string objectKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every active immutability record for the supplied bucket, ordered by
    /// <see cref="Cnas.Ps.Core.Domain.FileImmutabilityRecord.MarkedAtUtc"/> ascending.
    /// Intended for the admin "what is locked down" view; capped at
    /// <paramref name="limit"/> rows for safety.
    /// </summary>
    /// <param name="bucket">Storage bucket name.</param>
    /// <param name="limit">Maximum number of rows to return (clamped to [1, 500]).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of object keys flagged immutable in <paramref name="bucket"/>.</returns>
    Task<IReadOnlyList<string>> ListImmutableAsync(
        string bucket,
        int limit = 100,
        CancellationToken cancellationToken = default);
}
