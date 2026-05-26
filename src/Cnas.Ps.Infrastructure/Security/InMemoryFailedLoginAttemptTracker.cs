using System.Collections.Concurrent;
using Cnas.Ps.Application.Identity;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Security;

/// <summary>
/// In-memory <see cref="IFailedLoginAttemptTracker"/> backed by a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>. Singleton lifetime so the
/// counter survives across requests within a single replica. Sliding-window
/// expiry is enforced on read so stale entries are pruned lazily without a sweeper.
/// </summary>
/// <remarks>
/// <para>
/// <b>Swap to Redis when scaling out.</b> The interface lives in the Application
/// layer for exactly this reason — a multi-replica deployment will register a
/// Redis-backed implementation and the default registration in
/// <c>InfrastructureServiceCollectionExtensions.AddCnasInfrastructure</c> will be
/// conditionalised on the configured deployment mode. The current single-replica
/// process correctness is unaffected.
/// </para>
/// <para>
/// <b>Window contract.</b> An entry is "alive" while its
/// <c>(LastFailureUtc + Window)</c> is in the future. Past that instant the entry
/// is treated as count = 0 and is overwritten on the next failure. We do NOT
/// background-sweep — the worst-case memory cost is bounded by the user table size
/// and is recovered on every access.
/// </para>
/// </remarks>
public sealed class InMemoryFailedLoginAttemptTracker : IFailedLoginAttemptTracker
{
    /// <summary>Sliding-window length within which consecutive failures accumulate.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>Per-user state: current count + UTC instant of the last failure.</summary>
    private sealed record Entry(int Count, DateTime LastFailureUtc);

    /// <summary>Backing store; key is the internal user id.</summary>
    private readonly ConcurrentDictionary<long, Entry> _entries = new();

    /// <summary>UTC clock — never <see cref="DateTime.UtcNow"/> directly.</summary>
    private readonly ICnasTimeProvider _clock;

    /// <summary>Constructs the tracker around the supplied clock.</summary>
    /// <param name="clock">UTC clock abstraction (CLAUDE.md cross-cutting principle).</param>
    public InMemoryFailedLoginAttemptTracker(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <inheritdoc />
    public int RecordFailure(long userId)
    {
        var now = _clock.UtcNow;
        // AddOrUpdate atomically increments the counter; we discard an expired
        // entry by resetting the count to 1 instead of growing it indefinitely.
        var updated = _entries.AddOrUpdate(
            userId,
            _ => new Entry(1, now),
            (_, existing) =>
            {
                var expired = existing.LastFailureUtc + Window <= now;
                var newCount = expired ? 1 : existing.Count + 1;
                return new Entry(newCount, now);
            });
        return updated.Count;
    }

    /// <inheritdoc />
    public int GetFailureCount(long userId)
    {
        if (!_entries.TryGetValue(userId, out var entry))
        {
            return 0;
        }
        // Expire on read — the next failure will start a fresh window.
        return entry.LastFailureUtc + Window <= _clock.UtcNow ? 0 : entry.Count;
    }

    /// <inheritdoc />
    public void Reset(long userId) => _entries.TryRemove(userId, out _);
}
