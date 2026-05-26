using System.Collections.Concurrent;
using System.Security.Cryptography;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.E2E.Tests.Storage;

/// <summary>
/// Test-only in-memory <see cref="IFileStorage"/> implementation used by
/// <see cref="AuthenticatedApiHostFixture"/> so the UC17 phase 2A upload / download
/// E2E tests can exercise the full controller → service → storage pipeline without a
/// real MinIO broker. Production deployments wire
/// <c>Cnas.Ps.Infrastructure.Storage.MinioFileStorage</c> (or the fail-loud
/// <c>MissingMinioFileStorage</c> sentinel when credentials are absent); this class is
/// intentionally outside the production composition graph.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate implementation instead of reusing the missing-MinIO sentinel.</b>
/// The fixture historically wired <c>MissingMinioFileStorage</c>, which throws on first
/// use — that was acceptable while no E2E test touched the storage path. Phase 2A
/// changed that: the upload / download journey legitimately depends on
/// <see cref="PutAsync"/> and <see cref="GetAsync"/> round-tripping bytes. Rather than
/// stand up a MinIO container for CI, we substitute this in-memory dictionary in the
/// authenticated fixture's <c>ConfigureAdditionalSettings</c> hook. The basic
/// (unauthenticated) <c>ApiHostFixture</c> keeps the sentinel because the read-only
/// journeys still must not touch storage.
/// </para>
/// <para>
/// <b>Concurrency.</b> Backed by <see cref="ConcurrentDictionary{TKey, TValue}"/>; safe
/// for parallel test threads sharing the same fixture instance. Object keys are
/// composed by the caller (the service uses
/// <c>templates/{code}/v{version}/{code}.docx</c>) — this implementation accepts
/// arbitrary keys and returns them verbatim from <see cref="PutAsync"/> via
/// <see cref="StoredObject.ObjectKey"/> so the service-layer code path that uses the
/// composed key stays exercised. The service falls back to its composed key when the
/// returned key is empty, which this implementation never does.
/// </para>
/// <para>
/// <b>Presign behaviour.</b> No real HTTP server is bound; <see cref="PresignDownloadAsync"/>
/// returns a synthetic <c>inmemory://...</c> URI. The phase 2A E2E tests do not exercise
/// the presign path — downloads go through the streaming <c>GET /api/templates/{code}/download</c>
/// route, which reads via <see cref="GetAsync"/>.
/// </para>
/// </remarks>
public sealed class InMemoryFileStorage : IFileStorage
{
    /// <summary>
    /// Per-(bucket, object-key) byte payload. Composite string key keeps the dictionary
    /// flat; bucket and key are joined with a sentinel separator that cannot appear in
    /// either (MinIO bucket names are DNS-safe and S3 object keys disallow control
    /// chars, so '' is reliably outside both alphabets).
    /// </summary>
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    /// <summary>Composes the dictionary key from a (bucket, key) pair.</summary>
    private static string ComposeKey(string bucket, string objectKey) =>
        $"{bucket}{objectKey}";

    /// <inheritdoc />
    /// <remarks>
    /// Buffers the input stream into memory, computes the SHA-256 over the buffered
    /// bytes, and stores them under a deterministic random key. Returns the random
    /// key, the hex digest, and the size in the <see cref="StoredObject"/> result
    /// — same shape as <c>MinioFileStorage</c>.
    /// </remarks>
    public async Task<Result<StoredObject>> PutAsync(
        string bucket,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentNullException.ThrowIfNull(content);
        _ = contentType; // Captured for parity with MinioFileStorage; unused in tests.

        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        var bytes = ms.ToArray();

        // Random key per upload mirrors MinioFileStorage's GUID-based key — keeps the
        // service-layer code path (which trusts the storage-side key) honest in tests.
        var objectKey = $"inmem/{Guid.NewGuid():N}";
        _objects[ComposeKey(bucket, objectKey)] = bytes;

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return Result<StoredObject>.Success(new StoredObject(objectKey, sha, bytes.LongLength));
    }

    /// <inheritdoc />
    public Task<Result<Stream>> GetAsync(
        string bucket,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        _ = cancellationToken;

        if (_objects.TryGetValue(ComposeKey(bucket, objectKey), out var bytes))
        {
            return Task.FromResult(Result<Stream>.Success((Stream)new MemoryStream(bytes)));
        }
        return Task.FromResult(Result<Stream>.Failure(
            ErrorCodes.FileUnavailable, "Object not found in in-memory storage."));
    }

    /// <inheritdoc />
    public Task<Result<Uri>> PresignDownloadAsync(
        string bucket,
        string objectKey,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        _ = ttl;
        _ = cancellationToken;
        return Task.FromResult(Result<Uri>.Success(new Uri($"inmemory://{bucket}/{objectKey}")));
    }

    /// <inheritdoc />
    public Task<Result> DeleteAsync(
        string bucket,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        _ = cancellationToken;
        _objects.TryRemove(ComposeKey(bucket, objectKey), out _);
        return Task.FromResult(Result.Success());
    }
}
