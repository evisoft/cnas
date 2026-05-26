using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Backups;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services.Backups;

/// <summary>
/// R2307 / TOR SEC 060 — singleton in-memory implementation of
/// <see cref="IBackupTarget"/>. Holds payloads in a process-static
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> keyed by an opaque
/// storage key the adapter mints itself. Used by tests and as the default
/// production registration until DevOps swaps in the real S3 adapter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why singleton.</b> The dictionary is the storage; sharing one
/// instance across the host lifetime keeps the test fixtures honest
/// (upload then download must return the same bytes).
/// </para>
/// <para>
/// <b>Thread safety.</b> All operations are O(1) on a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>; the upload-and-hash
/// path is atomic per key.
/// </para>
/// </remarks>
public sealed class InMemoryBackupTarget : IBackupTarget
{
    private readonly ConcurrentDictionary<string, StoredPayload> _store = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public BackupTargetKind Kind => BackupTargetKind.InMemoryTest;

    /// <inheritdoc />
    public Task<Result<BackupUploadResult>> UploadAsync(
        BackupPolicy policy,
        BackupPayloadStream payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();

        // Mint a deterministic storage key — policy code + GUID so re-uploads
        // never collide and the operator can trace which policy a key came from.
        var key = $"inmem/{policy.PolicyCode}/{Guid.NewGuid():N}";
        var copy = payload.Payload.ToArray();
        _store[key] = new StoredPayload(copy, payload.Sha256Hex);

        var result = new BackupUploadResult(
            StorageKey: key,
            SizeBytes: copy.LongLength,
            Sha256Hex: payload.Sha256Hex);
        return Task.FromResult(Result<BackupUploadResult>.Success(result));
    }

    /// <inheritdoc />
    public Task<Result<BackupPayloadStream>> DownloadAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storageKey);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_store.TryGetValue(storageKey, out var stored))
        {
            return Task.FromResult(Result<BackupPayloadStream>.Failure(
                IBackupTarget.StorageKeyNotFoundCode,
                $"No payload found for storage key '{storageKey}'."));
        }

        // Re-hash the stored bytes so the orchestrator can compare against the expected hash
        // independently of the "echo" we returned at upload time. Mirrors how a real adapter
        // (S3 / Azure) would re-compute via streaming the persisted object.
        var actualHash = ComputeSha256Hex(stored.Bytes);
        var stream = new BackupPayloadStream(stored.Bytes, actualHash, stored.Bytes.LongLength);
        return Task.FromResult(Result<BackupPayloadStream>.Success(stream));
    }

    /// <inheritdoc />
    public Task<Result> DeleteAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storageKey);
        cancellationToken.ThrowIfCancellationRequested();

        // Idempotent — deletion of an absent key is success, matching object-store semantics.
        _store.TryRemove(storageKey, out _);
        return Task.FromResult(Result.Success());
    }

    /// <summary>Lowercase-hex SHA-256 digest of <paramref name="bytes"/>.</summary>
    /// <param name="bytes">Bytes to hash.</param>
    /// <returns>64-char hex string.</returns>
    internal static string ComputeSha256Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>Tuple wrapper around the dictionary value.</summary>
    /// <param name="Bytes">Stored payload bytes.</param>
    /// <param name="Sha256Hex">SHA-256 hex captured at upload time.</param>
    private sealed record StoredPayload(byte[] Bytes, string Sha256Hex);
}
