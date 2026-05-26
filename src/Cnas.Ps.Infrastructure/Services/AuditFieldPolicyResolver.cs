using System.Collections.Concurrent;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Reference implementation of <see cref="IAuditFieldPolicyResolver"/> backed by
/// a <see cref="ConcurrentDictionary{TKey, TValue}"/> snapshot rebuilt on a 60 s
/// cadence by <c>AuditFieldPolicyCacheRefreshJob</c> and on demand via
/// <see cref="InvalidateAsync"/> after every CRUD mutation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Snapshot atomicity.</b> The dictionary is held as a single instance and
/// replaced atomically by <see cref="System.Threading.Interlocked.Exchange{T}(ref T, T)"/>.
/// <see cref="Resolve"/> reads the field once so a refresh that completes mid-resolve
/// never produces a partially-updated view.
/// </para>
/// <para>
/// <b>Lifetime.</b> Registered as a singleton because the cache state must outlive
/// any single scope. The refresh job resolves the singleton via the DI scope factory.
/// </para>
/// </remarks>
public sealed class AuditFieldPolicyResolver : IAuditFieldPolicyResolver
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AuditFieldPolicyResolver> _logger;

    /// <summary>
    /// Current snapshot. Replaced atomically by <see cref="InvalidateAsync"/> and
    /// the background refresh job; read directly by <see cref="Resolve"/>. Starts
    /// empty so the resolver is safe to query before the first refresh completes —
    /// the no-policy-configured fall-through handles the empty-snapshot case.
    /// </summary>
    private ConcurrentDictionary<string, AuditFieldPolicyView> _snapshot =
        new(StringComparer.Ordinal);

    /// <summary>Constructs the resolver with its DI scope factory + logger.</summary>
    /// <param name="scopes">Scope factory used to materialise <see cref="IReadOnlyCnasDbContext"/> per refresh.</param>
    /// <param name="logger">Structured logger for refresh diagnostics.</param>
    public AuditFieldPolicyResolver(IServiceScopeFactory scopes, ILogger<AuditFieldPolicyResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _logger = logger;
    }

    /// <inheritdoc />
    public AuditFieldPolicyView? Resolve(string entityType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        var current = _snapshot;
        return current.TryGetValue(entityType, out var view) ? view : null;
    }

    /// <summary>
    /// Rebuilds the in-memory snapshot from the latest persisted state. Invoked by
    /// the background refresh job on its cadence and synchronously by the CRUD
    /// service after every successful mutation so the caller's change is visible
    /// to the next diff-write without waiting for the next refresh tick.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task that completes when the swap has happened.</returns>
    public async Task InvalidateAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IReadOnlyCnasDbContext>();

        var rows = await db.AuditFieldPolicies
            .Where(p => p.IsActive && p.IsEnabled)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var fresh = new ConcurrentDictionary<string, AuditFieldPolicyView>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var tracked = new HashSet<string>(
                (r.TrackedFields ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)),
                StringComparer.Ordinal);
            var suppressed = new HashSet<string>(
                (r.SuppressedFields ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)),
                StringComparer.Ordinal);

            var view = new AuditFieldPolicyView(
                EntityType: r.EntityType,
                TrackedFields: tracked,
                SuppressedFields: suppressed,
                RequireAnyChange: r.RequireAnyChange,
                Severity: r.Severity);

            // Last-writer-wins on duplicates; the DB unique index normally prevents
            // collisions but a hot-swap snapshot must remain robust.
            fresh[r.EntityType] = view;
        }

        Interlocked.Exchange(ref _snapshot, fresh);
        _logger.LogDebug(
            "AuditFieldPolicyResolver snapshot rebuilt with {Count} active policies.",
            fresh.Count);
    }

    /// <summary>
    /// Test seam — returns the current snapshot count. Used by integration tests
    /// to assert that <see cref="InvalidateAsync"/> picked up a newly inserted row.
    /// </summary>
    internal int SnapshotCount => _snapshot.Count;
}
