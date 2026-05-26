using System.Security.Cryptography;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace Cnas.Ps.Infrastructure.Storage;

/// <summary>
/// <see cref="IFileStorage"/> implementation backed by MinIO. Implements the security
/// hardening required by SEC 010 (magic-byte content type check happens upstream; this
/// layer generates random object keys and computes SHA-256 digests).
/// </summary>
/// <remarks>
/// The <see cref="ICnasTimeProvider"/> dependency drives the date-partitioned object-key
/// prefix (<c>yyyy/MM/dd</c>). Routing through the abstraction lets integration tests pin
/// the upload date and keeps the CLAUDE.md "UTC Everywhere" rule satisfied (no direct
/// <c>DateTime.UtcNow</c> outside the clock implementation itself).
/// </remarks>
public sealed class MinioFileStorage(
    IMinioClient client,
    IOptions<MinioOptions> options,
    ICnasTimeProvider clock,
    ILogger<MinioFileStorage> logger) : IFileStorage
{
    private readonly IMinioClient _client = client;
    private readonly MinioOptions _options = options.Value;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ILogger<MinioFileStorage> _logger = logger;

    /// <inheritdoc />
    public async Task<Result<StoredObject>> PutAsync(
        string bucket,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        if (content.CanSeek && content.Length > _options.MaxFileSizeBytes)
        {
            return Result<StoredObject>.Failure(ErrorCodes.FileTooLarge,
                $"File exceeds max size {_options.MaxFileSizeBytes} bytes.");
        }

        await EnsureBucketAsync(bucket, cancellationToken).ConfigureAwait(false);

        // Buffer to a temp file so we can compute SHA-256 then upload deterministically.
        var tempPath = Path.Combine(Path.GetTempPath(), $"cnas-{Guid.NewGuid():N}.bin");
        try
        {
            await using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81_920, useAsync: true))
            {
                await content.CopyToAsync(temp, cancellationToken).ConfigureAwait(false);
            }

            var sha256 = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);
            var size = new FileInfo(tempPath).Length;
            if (size > _options.MaxFileSizeBytes)
            {
                return Result<StoredObject>.Failure(ErrorCodes.FileTooLarge,
                    $"File exceeds max size {_options.MaxFileSizeBytes} bytes.");
            }

            var objectKey = $"{_clock.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}";

            await using (var upload = File.OpenRead(tempPath))
            {
                await _client.PutObjectAsync(
                    new PutObjectArgs()
                        .WithBucket(bucket)
                        .WithObject(objectKey)
                        .WithStreamData(upload)
                        .WithObjectSize(size)
                        .WithContentType(contentType),
                    cancellationToken).ConfigureAwait(false);
            }

            return Result<StoredObject>.Success(new StoredObject(objectKey, sha256, size));
        }
        finally
        {
            try { File.Delete(tempPath); } catch (IOException ex) { _logger.LogWarning(ex, "Failed to delete temp upload file."); }
        }
    }

    /// <inheritdoc />
    public async Task<Result<Stream>> GetAsync(string bucket, string objectKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var ms = new MemoryStream();
        try
        {
            await _client.GetObjectAsync(
                new GetObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(objectKey)
                    .WithCallbackStream(s => s.CopyTo(ms)),
                cancellationToken).ConfigureAwait(false);

            ms.Position = 0;
            return Result<Stream>.Success(ms);
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            await ms.DisposeAsync().ConfigureAwait(false);
            return Result<Stream>.Failure(ErrorCodes.FileUnavailable, "Object not found.");
        }
    }

    /// <inheritdoc />
    public async Task<Result<Uri>> PresignDownloadAsync(
        string bucket,
        string objectKey,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        // TTL guard:
        //   * Zero or negative TTL → reject (would mint a URL that's already expired
        //     and the previous code silently coerced negatives to a small positive
        //     int via the unchecked Math.Min cast).
        //   * Sub-second TTL → floor to 1s so we don't collapse to 0 and hand back a
        //     URL the upstream rejects as "expired on arrival".
        //   * Cap at 7 days (MinIO's documented maximum).
        if (ttl <= TimeSpan.Zero)
        {
            return Result<Uri>.Failure(ErrorCodes.ValidationFailed, "TTL must be positive.");
        }
        var ttlSeconds = (int)Math.Min(Math.Max(ttl.TotalSeconds, 1.0), 7 * 24 * 3600);

        var url = await _client.PresignedGetObjectAsync(
            new PresignedGetObjectArgs()
                .WithBucket(bucket)
                .WithObject(objectKey)
                .WithExpiry(ttlSeconds)).ConfigureAwait(false);

        return Result<Uri>.Success(new Uri(url));
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(string bucket, string objectKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        await _client.RemoveObjectAsync(
            new RemoveObjectArgs().WithBucket(bucket).WithObject(objectKey),
            cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    private async Task EnsureBucketAsync(string bucket, CancellationToken cancellationToken)
    {
        var exists = await _client.BucketExistsAsync(
            new BucketExistsArgs().WithBucket(bucket), cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            await _client.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
