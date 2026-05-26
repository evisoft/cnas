using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0137 — EF-Core-backed <see cref="IFileImmutabilityGuard"/>. Consulted by every
/// service that calls <see cref="IFileStorage.DeleteAsync"/> before forwarding the
/// delete; refuses the operation with the stable <see cref="ErrorCodes.ImmutableObject"/>
/// code when the addressed object carries an active immutability stamp.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> Registered as <c>Scoped</c> in DI because the underlying
/// <see cref="ICnasDbContext"/> is itself scoped (tracks per-request changes). The
/// guard accepts the write-side context rather than <see cref="IReadOnlyCnasDbContext"/>
/// so it does not require the read-replica routing topology to be configured — the
/// guard runs strictly off the primary so the immutability stamp written one HTTP call
/// ago is visible on the next.
/// </para>
/// <para>
/// <b>Read-your-own-writes.</b> Critical for correctness: a caller that just stamped
/// an object as immutable must be refused on a follow-up delete. Routing through the
/// streaming replica would risk a window of milliseconds where the replica lag hides
/// the new row from the guard.
/// </para>
/// </remarks>
/// <param name="db">EF Core write-side context (so reads see uncommitted writes from the same request).</param>
/// <param name="logger">Microsoft.Extensions logger.</param>
public sealed class FileImmutabilityGuard(
    ICnasDbContext db,
    ILogger<FileImmutabilityGuard> logger) : IFileImmutabilityGuard
{
    private readonly ICnasDbContext _db = db;
    private readonly ILogger<FileImmutabilityGuard> _logger = logger;

    /// <inheritdoc />
    public async Task<Result> CheckBeforeDeleteAsync(
        string bucket,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(objectKey))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Bucket and objectKey are required.");
        }

        var locked = await _db.FileImmutabilityRecords
            .AnyAsync(
                r => r.IsActive && r.Bucket == bucket && r.ObjectKey == objectKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (!locked)
        {
            return Result.Success();
        }

        _logger.LogInformation(
            "Refused delete on immutable object Bucket={Bucket} ObjectKey={ObjectKey}.",
            bucket, objectKey);
        return Result.Failure(
            ErrorCodes.ImmutableObject,
            $"Object {bucket}/{objectKey} is marked immutable and cannot be deleted.");
    }
}
