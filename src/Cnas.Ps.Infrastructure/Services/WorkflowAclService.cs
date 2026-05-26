using System.Collections.Concurrent;
using System.Collections.Generic;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.WorkflowAcl;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Reference implementation of <see cref="IWorkflowAclService"/>. Maintains two
/// atomic in-memory snapshots — one of workflow-level ACL fields keyed by workflow
/// id, one of step-level ACL rows keyed by (workflow id, step code) — and answers
/// every <see cref="CanHandleAsync"/> from them without paying for a DB round-trip.
/// The snapshots are rebuilt by <c>WorkflowAclCacheRefreshJob</c> on a 60 s cadence
/// (default) and synchronously by the CRUD service after every mutation via
/// <see cref="InvalidateAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Snapshot atomicity.</b> Each snapshot is a single
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> instance replaced atomically with
/// <see cref="Interlocked.Exchange{T}(ref T, T)"/>. Concurrent
/// <see cref="CanHandleAsync"/> readers always see a consistent map even when a
/// refresh swap completes mid-call.
/// </para>
/// <para>
/// <b>Super-admin override.</b> The first thing the gate checks is whether the
/// caller carries the <see cref="WorkflowAclConstants.SuperAdminRole"/> role; if so
/// it short-circuits to <c>true</c> regardless of the workflow/step ACL. This is the
/// break-glass invariant documented on <see cref="IWorkflowAclService"/>.
/// </para>
/// <para>
/// <b>Lifetime.</b> Registered as a singleton because the cache state must outlive
/// any single scope. The refresh job and CRUD service both invoke
/// <see cref="InvalidateAsync"/>; the implementation builds a fresh scope per refresh
/// so it can reach the scoped <see cref="IReadOnlyCnasDbContext"/>.
/// </para>
/// </remarks>
public sealed class WorkflowAclService : IWorkflowAclService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<WorkflowAclService> _logger;

    /// <summary>
    /// Workflow-level snapshot. Keyed by surrogate id; value carries the workflow's
    /// AllowedRoles + AllowedGroups projected as frozen sets for cheap intersection.
    /// </summary>
    private ConcurrentDictionary<long, WorkflowAclSnapshot> _workflowSnapshot = new();

    /// <summary>
    /// Step-level snapshot. Keyed by (workflow id, step code) so the gate can
    /// look up the per-step refinement in one hash hit.
    /// </summary>
    private ConcurrentDictionary<StepAclKey, StepAclSnapshot> _stepSnapshot = new();

    /// <summary>
    /// User snapshot. Keyed by surrogate user id; carries the role + group set as
    /// frozen <see cref="HashSet{T}"/>s so per-call intersection is O(min) without
    /// re-allocating on every check.
    /// </summary>
    private ConcurrentDictionary<long, UserRolesSnapshot> _userSnapshot = new();

    /// <summary>Constructs the resolver with its DI scope factory + logger.</summary>
    /// <param name="scopes">Scope factory used to materialise <see cref="IReadOnlyCnasDbContext"/> per refresh.</param>
    /// <param name="logger">Structured logger for refresh diagnostics.</param>
    public WorkflowAclService(IServiceScopeFactory scopes, ILogger<WorkflowAclService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> CanHandleAsync(
        long workflowDefinitionId,
        string stepCode,
        long userId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepCode);

        // Pull the caller's roles + groups. If the snapshot doesn't know the user yet
        // we fall back to a one-shot DB lookup so a freshly-created user never gets
        // a misleading deny on its very first action (the refresh job will pick the
        // row up on its next tick and subsequent calls will hit the cache).
        var userView = await ResolveUserAsync(userId, ct).ConfigureAwait(false);
        if (userView is null)
        {
            // No such user row — deny by default. The caller's higher layer (controller
            // policy) would normally catch this; the service is the second gate.
            return false;
        }

        // Super-admin escape hatch — break-glass override documented on the interface.
        if (userView.Roles.Contains(WorkflowAclConstants.SuperAdminRole))
        {
            return true;
        }

        // Workflow-level gate.
        var workflowView = _workflowSnapshot.TryGetValue(workflowDefinitionId, out var w) ? w : null;
        if (workflowView is null)
        {
            // No workflow row in cache — fall back to a one-shot lookup. The workflow
            // may have been published after the last refresh tick.
            workflowView = await ResolveWorkflowAsync(workflowDefinitionId, ct).ConfigureAwait(false);
        }
        if (workflowView is null)
        {
            // Workflow truly absent — deny rather than implicit allow. The controller
            // layer would normally have surfaced 404 already; we err on the safe side.
            return false;
        }

        // When the workflow has opted into ACLs (either role or group list non-empty)
        // the caller MUST intersect at least one side. Empty workflow-level lists mean
        // "legacy fallback" — the gate is satisfied without intersection.
        var workflowGated = workflowView.AllowedRoles.Count > 0 || workflowView.AllowedGroups.Count > 0;
        if (workflowGated)
        {
            var passesWorkflowRole = workflowView.AllowedRoles.Count == 0
                || workflowView.AllowedRoles.Overlaps(userView.Roles);
            var passesWorkflowGroup = workflowView.AllowedGroups.Count == 0
                || workflowView.AllowedGroups.Overlaps(userView.Groups);
            // A workflow that lists BOTH roles and groups requires intersection on at
            // least one of the two (logical OR within the workflow gate). A workflow
            // with only roles or only groups requires intersection on whichever list
            // is non-empty.
            var roleListsConfigured = workflowView.AllowedRoles.Count > 0;
            var groupListsConfigured = workflowView.AllowedGroups.Count > 0;
            if (roleListsConfigured && groupListsConfigured)
            {
                if (!(workflowView.AllowedRoles.Overlaps(userView.Roles)
                      || workflowView.AllowedGroups.Overlaps(userView.Groups)))
                {
                    return false;
                }
            }
            else
            {
                if (roleListsConfigured && !passesWorkflowRole) return false;
                if (groupListsConfigured && !passesWorkflowGroup) return false;
            }
        }

        // Step-level refinement. Absence of a step ACL row means "no extra requirement".
        var stepKey = new StepAclKey(workflowDefinitionId, stepCode);
        if (_stepSnapshot.TryGetValue(stepKey, out var stepView))
        {
            // RequiredRoles — when non-empty, caller must intersect.
            if (stepView.RequiredRoles.Count > 0
                && !stepView.RequiredRoles.Overlaps(userView.Roles))
            {
                return false;
            }
            // RequiredGroups — when non-empty, caller must intersect.
            if (stepView.RequiredGroups.Count > 0
                && !stepView.RequiredGroups.Overlaps(userView.Groups))
            {
                return false;
            }
            // RequiredPermission — single explicit code that must appear in roles.
            if (!string.IsNullOrEmpty(stepView.RequiredPermission)
                && !userView.Roles.Contains(stepView.RequiredPermission))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public async Task InvalidateAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IReadOnlyCnasDbContext>();

        // ── Workflow-level snapshot ──
        var workflows = await db.WorkflowDefinitions
            .Where(w => w.IsActive)
            .Select(w => new
            {
                w.Id,
                w.AllowedRoles,
                w.AllowedGroups,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var nextWorkflowSnapshot = new ConcurrentDictionary<long, WorkflowAclSnapshot>();
        foreach (var w in workflows)
        {
            nextWorkflowSnapshot[w.Id] = new WorkflowAclSnapshot(
                AllowedRoles: ToFrozenSet(w.AllowedRoles),
                AllowedGroups: ToFrozenSet(w.AllowedGroups));
        }
        Interlocked.Exchange(ref _workflowSnapshot, nextWorkflowSnapshot);

        // ── Step-level snapshot ──
        var steps = await db.WorkflowStepAcls
            .Where(s => s.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var nextStepSnapshot = new ConcurrentDictionary<StepAclKey, StepAclSnapshot>();
        foreach (var s in steps)
        {
            nextStepSnapshot[new StepAclKey(s.WorkflowDefinitionId, s.StepCode)] = new StepAclSnapshot(
                RequiredRoles: ToFrozenSet(s.RequiredRoles),
                RequiredGroups: ToFrozenSet(s.RequiredGroups),
                RequiredPermission: s.RequiredPermission);
        }
        Interlocked.Exchange(ref _stepSnapshot, nextStepSnapshot);

        // ── User snapshot ──
        // Note: large user bases would push this out-of-band, but at the present scale
        // (≤10k staff) a full reload is cheap and dramatically simplifies the gate.
        var users = await db.UserProfiles
            .Where(u => u.IsActive)
            .Select(u => new { u.Id, u.Roles, u.Groups })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var nextUserSnapshot = new ConcurrentDictionary<long, UserRolesSnapshot>();
        foreach (var u in users)
        {
            nextUserSnapshot[u.Id] = new UserRolesSnapshot(
                Roles: ToFrozenSet(u.Roles),
                Groups: ToFrozenSet(u.Groups));
        }
        Interlocked.Exchange(ref _userSnapshot, nextUserSnapshot);

        _logger.LogDebug(
            "WorkflowAclService snapshot rebuilt: {Workflows} workflows, {Steps} step ACLs, {Users} users.",
            nextWorkflowSnapshot.Count, nextStepSnapshot.Count, nextUserSnapshot.Count);
    }

    /// <summary>
    /// One-shot user lookup used when the cache hasn't yet picked up a freshly
    /// created user row. Reads the persisted UserProfile and returns a frozen
    /// projection; falls through to <c>null</c> when the id is unknown.
    /// </summary>
    /// <param name="userId">Internal user id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The frozen snapshot, or <c>null</c> when the user is absent.</returns>
    private async Task<UserRolesSnapshot?> ResolveUserAsync(long userId, CancellationToken ct)
    {
        if (_userSnapshot.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IReadOnlyCnasDbContext>();
        var row = await db.UserProfiles
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => new { u.Roles, u.Groups })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (row is null) return null;
        var view = new UserRolesSnapshot(
            Roles: ToFrozenSet(row.Roles),
            Groups: ToFrozenSet(row.Groups));
        _userSnapshot[userId] = view;
        return view;
    }

    /// <summary>
    /// One-shot workflow lookup used when the cache hasn't yet picked up a freshly
    /// published workflow definition row.
    /// </summary>
    /// <param name="workflowDefinitionId">Internal workflow definition id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The frozen ACL snapshot, or <c>null</c> when absent.</returns>
    private async Task<WorkflowAclSnapshot?> ResolveWorkflowAsync(long workflowDefinitionId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IReadOnlyCnasDbContext>();
        var row = await db.WorkflowDefinitions
            .Where(w => w.Id == workflowDefinitionId && w.IsActive)
            .Select(w => new { w.AllowedRoles, w.AllowedGroups })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (row is null) return null;
        var view = new WorkflowAclSnapshot(
            AllowedRoles: ToFrozenSet(row.AllowedRoles),
            AllowedGroups: ToFrozenSet(row.AllowedGroups));
        _workflowSnapshot[workflowDefinitionId] = view;
        return view;
    }

    /// <summary>
    /// Converts a possibly-null list into a frozen <see cref="HashSet{T}"/> with
    /// ordinal comparison. Centralised so every snapshot uses identical comparison
    /// semantics.
    /// </summary>
    /// <param name="items">Input list (may be null).</param>
    /// <returns>An ordinal-comparison hash set (never null).</returns>
    private static HashSet<string> ToFrozenSet(IEnumerable<string>? items)
    {
        if (items is null) return new HashSet<string>(StringComparer.Ordinal);
        return new HashSet<string>(items, StringComparer.Ordinal);
    }

    /// <summary>
    /// Test seam — returns the current step-ACL snapshot size. Used by integration
    /// tests to assert that <see cref="InvalidateAsync"/> picked up a newly inserted row.
    /// </summary>
    internal int StepSnapshotCount => _stepSnapshot.Count;

    /// <summary>Test seam — returns the current workflow snapshot size.</summary>
    internal int WorkflowSnapshotCount => _workflowSnapshot.Count;

    /// <summary>
    /// Workflow-level snapshot projection. The two hash sets are built once at
    /// refresh time and shared across every read of this workflow.
    /// </summary>
    /// <param name="AllowedRoles">Workflow-level allowed roles (frozen).</param>
    /// <param name="AllowedGroups">Workflow-level allowed groups (frozen).</param>
    private sealed record WorkflowAclSnapshot(
        HashSet<string> AllowedRoles,
        HashSet<string> AllowedGroups);

    /// <summary>
    /// Step-level snapshot projection. The optional explicit permission is stored as
    /// a plain string rather than a single-entry set so the gate can branch on
    /// <c>string.IsNullOrEmpty</c> without allocating.
    /// </summary>
    /// <param name="RequiredRoles">Step-level required roles (frozen).</param>
    /// <param name="RequiredGroups">Step-level required groups (frozen).</param>
    /// <param name="RequiredPermission">Optional single permission code.</param>
    private sealed record StepAclSnapshot(
        HashSet<string> RequiredRoles,
        HashSet<string> RequiredGroups,
        string? RequiredPermission);

    /// <summary>
    /// User-side snapshot projection. Carries the role + group set as frozen hash
    /// sets so per-call intersection is O(min(workflow.roles, user.roles)) without
    /// re-allocating.
    /// </summary>
    /// <param name="Roles">Role codes the user carries.</param>
    /// <param name="Groups">Group codes the user belongs to.</param>
    private sealed record UserRolesSnapshot(HashSet<string> Roles, HashSet<string> Groups);

    /// <summary>
    /// Composite key used by the step-ACL snapshot. Ordinal string comparison on
    /// <see cref="StepCode"/> matches the canonical case-sensitive BPMN activity-id
    /// vocabulary.
    /// </summary>
    /// <param name="WorkflowDefinitionId">Workflow surrogate id.</param>
    /// <param name="StepCode">Canonical step code.</param>
    private readonly record struct StepAclKey(long WorkflowDefinitionId, string StepCode);
}
