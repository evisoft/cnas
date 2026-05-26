using Cnas.Ps.Application.Attachments;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Storage;

/// <summary>
/// R0227 / TOR UI 014 — local-disk implementation of <see cref="IBlobStorage"/> used
/// by the attachment subsystem in dev / staging / test deployments. Production
/// deployments will swap to a MinIO / S3 adapter (deferred — the MinIO-backed
/// <c>MinioFileStorage</c> exists already for the document subsystem and can be
/// adapted in a follow-up batch).
/// </summary>
/// <remarks>
/// <para>
/// <b>Path-traversal guard.</b> Every operation resolves the candidate key to an
/// absolute path under the configured <see cref="AttachmentOptions.RootPath"/> and
/// refuses to act if the resolved path escapes the root (defence against keys like
/// <c>../../etc/passwd</c>). The guard runs even on
/// <see cref="DeleteAsync"/> so a poisoned row cannot weaponise the cleanup path.
/// </para>
/// <para>
/// <b>Idempotent delete.</b> <see cref="DeleteAsync"/> silently succeeds when the
/// target file is missing — matches the <see cref="IBlobStorage"/> contract so the
/// service-layer cleanup cascade does not fail spuriously.
/// </para>
/// </remarks>
public sealed class LocalDiskBlobStorage : IBlobStorage
{
    private readonly AttachmentOptions _options;

    /// <summary>Builds the adapter from the bound <see cref="AttachmentOptions"/>.</summary>
    /// <param name="options">Configured options instance.</param>
    public LocalDiskBlobStorage(IOptions<AttachmentOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task PutAsync(string key, byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(bytes);

        var resolved = ResolveSafe(key);
        var dir = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        await File.WriteAllBytesAsync(resolved, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<byte[]> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var resolved = ResolveSafe(key);
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException("Attachment blob not found.", resolved);
        }
        return await File.ReadAllBytesAsync(resolved, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _ = cancellationToken;

        var resolved = ResolveSafe(key);
        if (File.Exists(resolved))
        {
            File.Delete(resolved);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves a candidate <paramref name="key"/> to an absolute path under
    /// <see cref="AttachmentOptions.RootPath"/> and throws
    /// <see cref="UnauthorizedAccessException"/> if the resolved path escapes the
    /// root (path-traversal guard).
    /// </summary>
    /// <param name="key">Caller-supplied storage key.</param>
    /// <returns>Validated absolute path under the configured root.</returns>
    /// <exception cref="UnauthorizedAccessException">When the key escapes the root.</exception>
    private string ResolveSafe(string key)
    {
        if (key.Contains("..", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Storage key cannot contain '..' segments.");
        }

        var root = Path.GetFullPath(_options.RootPath);
        var candidate = Path.GetFullPath(Path.Combine(root, key));

        // Path.GetFullPath normalises away "." / ".." — but we still re-verify the
        // resolved path lives under the root. A naive prefix check accepts sibling
        // directories whose name starts with the root (e.g. root="C:\storage\blobs"
        // accepts "C:\storage\blobsX\..."). We require either an exact match (the
        // root itself, which is permissible only for directory-level operations) or
        // a path under the root followed by a directory separator.
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSep, StringComparison.Ordinal)
            && !string.Equals(candidate, root, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Storage key escapes the configured root.");
        }
        return candidate;
    }
}
