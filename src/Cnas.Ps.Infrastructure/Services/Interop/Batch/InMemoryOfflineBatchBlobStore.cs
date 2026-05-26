using System.Collections.Concurrent;
using Cnas.Ps.Application.Interop.Batch;

namespace Cnas.Ps.Infrastructure.Services.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — process-local in-memory implementation of
/// <see cref="IOfflineBatchBlobStore"/>. Used by the default DI registration
/// AND every test fixture; production deployments replace it with an
/// S3 / MinIO adapter in a follow-up batch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a thread-safe dictionary.</b> The Quartz job and the consumer
/// HTTP path run concurrently in the same process — the
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> guarantees neither
/// writer races the other. Storage keys are Guid-based to avoid collisions.
/// </para>
/// </remarks>
public sealed class InMemoryOfflineBatchBlobStore : IOfflineBatchBlobStore
{
    private readonly ConcurrentDictionary<string, byte[]> _payloads = new();

    /// <inheritdoc />
    public Task<string> PutAsync(
        byte[] payload,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(contentType);
        // Defensive copy so a caller cannot mutate the persisted payload
        // after the fact (the InMemoryOfflineBatchBlobStore is shared across
        // requests).
        var copy = new byte[payload.Length];
        Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
        var key = $"obb-{Guid.NewGuid():N}";
        _payloads[key] = copy;
        return Task.FromResult(key);
    }

    /// <inheritdoc />
    public Task<byte[]> GetAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        if (!_payloads.TryGetValue(storageKey, out var payload))
        {
            throw new FileNotFoundException($"Unknown offline-batch blob storage key '{storageKey}'.");
        }
        // Defensive copy — callers receive an isolated buffer they can mutate.
        var copy = new byte[payload.Length];
        Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
        return Task.FromResult(copy);
    }
}
