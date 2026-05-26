using System.Collections.Concurrent;
using System.Collections.Generic;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.WorkflowNotifications;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Reference implementation of <see cref="IWorkflowNotificationStrategyResolver"/>.
/// Maintains an in-memory snapshot of the
/// <see cref="WorkflowNotificationStrategy"/> table that the workflow orchestrator
/// consults on every dispatch without paying for a DB round-trip. The snapshot is
/// rebuilt by the <c>WorkflowNotificationStrategyCacheRefreshJob</c> background service
/// on a 60 s cadence by default; the CRUD service additionally triggers a synchronous
/// refresh via <see cref="InvalidateAsync"/> after every mutation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Snapshot atomicity.</b> The snapshot is a single
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> instance; refresh replaces the
/// instance with <see cref="Interlocked.Exchange{T}(ref T, T)"/> so concurrent
/// <see cref="Resolve"/> readers always see a consistent map. Reading via
/// <see cref="ConcurrentDictionary{TKey, TValue}.TryGetValue"/> is lock-free.
/// </para>
/// <para>
/// <b>Lifetime.</b> Registered as a singleton because the cache state must outlive any
/// single scope. The refresh job resolves the singleton via the DI scope factory.
/// </para>
/// </remarks>
public sealed class WorkflowNotificationStrategyResolver : IWorkflowNotificationStrategyResolver
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<WorkflowNotificationStrategyResolver> _logger;

    /// <summary>
    /// Current snapshot. Keyed by (WorkflowDefinitionId, EventCode). Replaced atomically by
    /// <see cref="InvalidateAsync"/> and the background refresh job; read directly by
    /// <see cref="Resolve"/>. Starts as an empty map so the resolver is safe to query
    /// before the first refresh completes — the null-on-miss contract handles the
    /// no-match case.
    /// </summary>
    private ConcurrentDictionary<StrategyKey, WorkflowNotificationStrategyView> _snapshot = new();

    /// <summary>Constructs the resolver with its DI scope factory + logger.</summary>
    /// <param name="scopes">Scope factory used to materialise <see cref="IReadOnlyCnasDbContext"/> per refresh.</param>
    /// <param name="logger">Structured logger for refresh diagnostics.</param>
    public WorkflowNotificationStrategyResolver(
        IServiceScopeFactory scopes,
        ILogger<WorkflowNotificationStrategyResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _logger = logger;
    }

    /// <inheritdoc />
    public WorkflowNotificationStrategyView? Resolve(long workflowDefinitionId, string eventCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventCode);

        var current = _snapshot;
        return current.TryGetValue(new StrategyKey(workflowDefinitionId, eventCode), out var view)
            ? view
            : null;
    }

    /// <summary>
    /// Rebuilds the in-memory snapshot from the latest persisted state. Invoked by the
    /// background refresh job on its cadence and synchronously by
    /// <see cref="IWorkflowNotificationStrategyService"/> after every successful
    /// mutation so the caller's change is visible to the next workflow dispatch
    /// without waiting for the next refresh tick.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task that completes when the swap has happened.</returns>
    public async Task InvalidateAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IReadOnlyCnasDbContext>();

        var rows = await db.WorkflowNotificationStrategies
            .Where(s => s.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var next = new ConcurrentDictionary<StrategyKey, WorkflowNotificationStrategyView>();
        foreach (var r in rows)
        {
            // Defensive copy: the entity's lists belong to the EF change tracker; the
            // resolver caches frozen IReadOnlyList projections so the orchestrator can
            // never accidentally mutate cached state.
            var channels = (r.Channels ?? new List<NotificationChannel>()).ToArray();
            var roles = (r.RecipientRoles ?? new List<string>()).ToArray();

            (int Start, int End)? quietHours =
                r.QuietHoursStartLocalMinute is int start && r.QuietHoursEndLocalMinute is int end
                    ? (start, end)
                    : null;

            var view = new WorkflowNotificationStrategyView(
                IsEnabled: r.IsEnabled,
                Channels: channels,
                RecipientRoles: roles,
                TemplateCodeOverride: r.TemplateCodeOverride,
                QuietHours: quietHours);

            next[new StrategyKey(r.WorkflowDefinitionId, r.EventCode)] = view;
        }

        Interlocked.Exchange(ref _snapshot, next);
        _logger.LogDebug(
            "WorkflowNotificationStrategy snapshot rebuilt with {Count} active strategies.",
            next.Count);
    }

    /// <summary>
    /// Test seam — returns the current snapshot size. Used by integration tests to
    /// assert that <see cref="InvalidateAsync"/> picked up a newly inserted row.
    /// </summary>
    internal int SnapshotCount => _snapshot.Count;

    /// <summary>
    /// Composite key used by the snapshot dictionary. Ordinal string comparison on
    /// <see cref="EventCode"/> matches the canonical case-sensitive event vocabulary
    /// in <see cref="WorkflowNotificationEvents"/>.
    /// </summary>
    /// <param name="WorkflowDefinitionId">Workflow definition surrogate id.</param>
    /// <param name="EventCode">Canonical event code.</param>
    private readonly record struct StrategyKey(long WorkflowDefinitionId, string EventCode);
}
