using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// R0137 — gatekeeper consulted BEFORE any delete-shaped operation on the object
/// storage backend. Refuses a delete when the addressed object carries an active
/// application-level immutability stamp (see <see cref="IFileImmutabilityMarker"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where to wire it.</b> Any service that calls <see cref="IFileStorage.DeleteAsync"/>
/// must first call <see cref="CheckBeforeDeleteAsync"/> and propagate the failure when
/// it returns one. The guard does not call <see cref="IFileStorage.DeleteAsync"/> on
/// the caller's behalf — composition is left to the caller because some callers want
/// to short-circuit silently (e.g. during a bulk-delete) while others must surface the
/// rejection as a user-facing error.
/// </para>
/// <para>
/// <b>Why a separate type from the marker.</b> Splitting the read-only guard from the
/// write-side marker keeps the "before every delete" path zero-dependency on the
/// write path — the guard only needs <see cref="IReadOnlyCnasDbContext"/>, which lets
/// callers register it as a singleton against the streaming replica without forcing
/// them to also pull in the primary context.
/// </para>
/// </remarks>
public interface IFileImmutabilityGuard
{
    /// <summary>
    /// Returns success when the supplied <c>(bucket, objectKey)</c> is safe to delete;
    /// failure with the stable code <c>FILESTORAGE.IMMUTABLE_OBJECT</c> when the object
    /// carries an active immutability stamp.
    /// </summary>
    /// <param name="bucket">Storage bucket name.</param>
    /// <param name="objectKey">Object key within the bucket.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>Result.Success</c> when the object is not marked immutable.
    /// <c>Result.Failure(ErrorCodes.ImmutableObject)</c> when it is.
    /// <c>Result.Failure(ErrorCodes.ValidationFailed)</c> when either input is blank.
    /// </returns>
    Task<Result> CheckBeforeDeleteAsync(
        string bucket,
        string objectKey,
        CancellationToken cancellationToken = default);
}
