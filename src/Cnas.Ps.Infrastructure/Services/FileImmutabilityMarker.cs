using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0137 — EF-Core-backed implementation of <see cref="IFileImmutabilityMarker"/>.
/// Writes <see cref="FileImmutabilityRecord"/> rows keyed by <c>(bucket, objectKey)</c>
/// and exposes a small query surface for the admin "what is locked down" view.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> Registered as <c>Scoped</c> in DI because the EF Core
/// <see cref="ICnasDbContext"/> dependency is itself scoped and tracks per-request
/// changes. The <see cref="ICnasTimeProvider"/> abstraction backs the <c>MarkedAtUtc</c>
/// stamp so tests can pin the clock and the production code stays compliant with
/// CLAUDE.md's "UTC Everywhere" rule (no direct <c>DateTime.UtcNow</c>).
/// </para>
/// <para>
/// <b>Idempotency.</b> A second mark for the same <c>(bucket, objectKey)</c> pair
/// short-circuits to a no-op success — the implementation checks for an existing
/// <c>IsActive=true</c> row before adding a new one. The partial unique index on
/// the underlying table backs this contract in production even if a race between two
/// concurrent marks slips past the in-application check (Postgres rejects the second
/// insert with a unique-violation, which the caller catches and folds into success).
/// </para>
/// </remarks>
/// <param name="db">EF Core write-side context.</param>
/// <param name="clock">Time provider used for the <c>MarkedAtUtc</c> stamp.</param>
/// <param name="caller">Caller context — used to capture the marking user id when known.</param>
/// <param name="logger">Microsoft.Extensions logger.</param>
public sealed class FileImmutabilityMarker(
    ICnasDbContext db,
    ICnasTimeProvider clock,
    ICallerContext caller,
    ILogger<FileImmutabilityMarker> logger) : IFileImmutabilityMarker
{
    private readonly ICnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly ILogger<FileImmutabilityMarker> _logger = logger;

    /// <inheritdoc />
    public async Task<Result> MarkImmutableAsync(
        string bucket,
        string objectKey,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(objectKey))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Bucket and objectKey are required.");
        }

        // Idempotent: if an active mark already exists, the call is a no-op success.
        var exists = await _db.FileImmutabilityRecords
            .AnyAsync(
                r => r.IsActive && r.Bucket == bucket && r.ObjectKey == objectKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (exists)
        {
            return Result.Success();
        }

        var now = _clock.UtcNow;
        var row = new FileImmutabilityRecord
        {
            Bucket = bucket,
            ObjectKey = objectKey,
            MarkedAtUtc = now,
            MarkedByUserId = _caller.UserId,
            Reason = reason,
            CreatedAtUtc = now,
            IsActive = true,
        };
        _db.FileImmutabilityRecords.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // INFO: the bucket + key alone are operationally safe to log; no PII in either.
        _logger.LogInformation(
            "FileImmutability marker recorded for Bucket={Bucket} ObjectKey={ObjectKey} by UserId={UserId}.",
            bucket, objectKey, _caller.UserId);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<bool> IsImmutableAsync(
        string bucket,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(objectKey))
        {
            return false;
        }

        return await _db.FileImmutabilityRecords
            .AnyAsync(
                r => r.IsActive && r.Bucket == bucket && r.ObjectKey == objectKey,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListImmutableAsync(
        string bucket,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket))
        {
            return Array.Empty<string>();
        }

        var capped = Math.Clamp(limit, 1, 500);
        return await _db.FileImmutabilityRecords
            .Where(r => r.IsActive && r.Bucket == bucket)
            .OrderBy(r => r.MarkedAtUtc)
            .Select(r => r.ObjectKey)
            .Take(capped)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
