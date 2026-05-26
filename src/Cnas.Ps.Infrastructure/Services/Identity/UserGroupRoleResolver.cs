using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Identity;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Identity;

/// <summary>
/// R2270 / TOR SEC 023-024 — concrete implementation of
/// <see cref="IUserGroupRoleResolver"/>. Walks the <c>UserGroupParent</c>
/// DAG from a user's direct memberships and unions every reachable active
/// ancestor group's role grant.
/// </summary>
/// <remarks>
/// <para>
/// <b>Defence-in-depth cycle detection.</b> The write-side service rejects
/// cycle-creating add-child requests, but the resolver still tracks every
/// visited group id during BFS and short-circuits on revisits. When a
/// revisit happens (a cycle slipped through somehow) the resolver emits a
/// Critical <c>USER_GROUP.CYCLE_DETECTED</c> audit row so operators can
/// triage the registry repair.
/// </para>
/// <para>
/// <b>Disabled groups.</b> A group whose
/// <see cref="UserGroup.Status"/> is <see cref="UserGroupStatus.Disabled"/>
/// contributes NO roles. The traversal still walks through it so a disabled
/// intermediary does not silently hide a re-enabled ancestor's grants.
/// </para>
/// </remarks>
public sealed class UserGroupRoleResolver : IUserGroupRoleResolver
{
    /// <summary>Stable audit event code emitted on defence-in-depth cycle detection.</summary>
    public const string AuditCycleDetected = "USER_GROUP.CYCLE_DETECTED";

    /// <summary>Cached JSON serializer options shared across audit-payload builders.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly ISqidService _sqids;
    private readonly IAuditService _audit;

    /// <summary>Constructs the resolver with its collaborators.</summary>
    /// <param name="db">EF Core context — used for read access only.</param>
    /// <param name="sqids">Sqid encoder for the projected DTOs.</param>
    /// <param name="audit">Audit-journal façade for the defence-in-depth cycle alert.</param>
    public UserGroupRoleResolver(ICnasDbContext db, ISqidService sqids, IAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(audit);
        _db = db;
        _sqids = sqids;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<Result<UserGroupEffectiveRolesDto>> ResolveEffectiveRolesAsync(long userProfileId, CancellationToken ct = default)
    {
        // 1) Direct memberships for the user.
        var directGroupIds = await _db.UserGroupMemberships
            .Where(m => m.UserProfileId == userProfileId && m.IsActive)
            .Select(m => m.UserGroupId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (directGroupIds.Count == 0)
        {
            CnasMeter.UserGroupRoleResolved.Add(1,
                new KeyValuePair<string, object?>("cache_hit", false));
            return Result<UserGroupEffectiveRolesDto>.Success(new UserGroupEffectiveRolesDto(
                UserSqid: _sqids.Encode(userProfileId),
                Roles: Array.Empty<UserGroupEffectiveRoleDto>()));
        }

        // 2) Walk upward through UserGroupParents, recording the chain so we
        // can attribute each role to the group that contributed it.
        var visited = new HashSet<long>();
        var orderedReachable = new List<long>();
        // chainByGroup[g] = ordered chain of group ids from the user's direct
        // group to g (inclusive of both endpoints).
        var chainByGroup = new Dictionary<long, List<long>>();

        var frontier = new Queue<(long Id, List<long> Chain)>();
        foreach (var directId in directGroupIds)
        {
            if (visited.Add(directId))
            {
                var chain = new List<long> { directId };
                chainByGroup[directId] = chain;
                orderedReachable.Add(directId);
                frontier.Enqueue((directId, chain));
            }
        }

        var cycleDetectedDuringTraversal = false;
        while (frontier.Count > 0)
        {
            var (current, currentChain) = frontier.Dequeue();
            var parents = await _db.UserGroupParents
                .Where(p => p.ChildGroupId == current && p.IsActive)
                .Select(p => p.ParentGroupId)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var parentId in parents)
            {
                if (currentChain.Contains(parentId))
                {
                    // Defence in depth — should never happen because the
                    // write-side service rejects cycles.
                    cycleDetectedDuringTraversal = true;
                    continue;
                }
                if (visited.Add(parentId))
                {
                    var nextChain = new List<long>(currentChain.Count + 1);
                    nextChain.AddRange(currentChain);
                    nextChain.Add(parentId);
                    chainByGroup[parentId] = nextChain;
                    orderedReachable.Add(parentId);
                    frontier.Enqueue((parentId, nextChain));
                }
            }
        }

        if (cycleDetectedDuringTraversal)
        {
            var payload = JsonSerializer.Serialize(new
            {
                userSqid = _sqids.Encode(userProfileId),
                directGroupCount = directGroupIds.Count,
            }, CachedJsonOptions);
            await _audit.RecordAsync(
                AuditCycleDetected,
                AuditSeverity.Critical,
                "system",
                nameof(UserGroup),
                userProfileId,
                payload,
                sourceIp: null,
                correlationId: null,
                ct).ConfigureAwait(false);
        }

        // 3) Load all reachable groups in one query and build the role list.
        var reachableSet = orderedReachable.ToHashSet();
        var groups = await _db.UserGroups
            .Where(g => reachableSet.Contains(g.Id) && g.IsActive)
            .Select(g => new { g.Id, g.Code, g.Status, g.Roles })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var groupsById = groups.ToDictionary(g => g.Id);

        // Walk reachable groups in BFS-insertion order; collect distinct roles
        // and remember the first chain that produced each (so the chain is
        // the shortest path from the user's direct group to the granting group).
        var perRole = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var groupId in orderedReachable)
        {
            if (!groupsById.TryGetValue(groupId, out var grp))
            {
                continue;
            }
            // Disabled groups contribute no roles.
            if (grp.Status == UserGroupStatus.Disabled)
            {
                continue;
            }
            if (grp.Roles is null)
            {
                continue;
            }
            var chainIds = chainByGroup[groupId];
            // Convert the chain of ids → chain of codes (skip ids that did not load — e.g. soft-deleted ancestor).
            var chainCodes = new List<string>(chainIds.Count);
            foreach (var id in chainIds)
            {
                if (groupsById.TryGetValue(id, out var g))
                {
                    chainCodes.Add(g.Code);
                }
            }
            foreach (var role in grp.Roles)
            {
                if (!perRole.ContainsKey(role))
                {
                    perRole[role] = chainCodes;
                }
            }
        }

        var roleDtos = perRole
            .Select(kv => new UserGroupEffectiveRoleDto(kv.Key, kv.Value))
            .ToList();

        CnasMeter.UserGroupRoleResolved.Add(1,
            new KeyValuePair<string, object?>("cache_hit", false));

        return Result<UserGroupEffectiveRolesDto>.Success(new UserGroupEffectiveRolesDto(
            UserSqid: _sqids.Encode(userProfileId),
            Roles: roleDtos));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<UserGroupDto>>> ResolveAncestorsAsync(long groupId, CancellationToken ct = default)
    {
        var ancestors = await WalkAsync(groupId, walkUp: true, ct).ConfigureAwait(false);
        return Result<IReadOnlyList<UserGroupDto>>.Success(ancestors);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<UserGroupDto>>> ResolveDescendantsAsync(long groupId, CancellationToken ct = default)
    {
        var descendants = await WalkAsync(groupId, walkUp: false, ct).ConfigureAwait(false);
        return Result<IReadOnlyList<UserGroupDto>>.Success(descendants);
    }

    /// <summary>
    /// Shared BFS traversal used by ancestor / descendant resolution.
    /// </summary>
    /// <param name="startId">Starting group id.</param>
    /// <param name="walkUp">When true, follows <c>ChildGroupId → ParentGroupId</c>; when false, follows the reverse direction.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Ordered list of reachable groups (excluding the start node).</returns>
    private async Task<IReadOnlyList<UserGroupDto>> WalkAsync(long startId, bool walkUp, CancellationToken ct)
    {
        var visited = new HashSet<long> { startId };
        var orderedReachable = new List<long>();
        var frontier = new Queue<long>();
        frontier.Enqueue(startId);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            var nextIds = walkUp
                ? await _db.UserGroupParents
                    .Where(p => p.ChildGroupId == current && p.IsActive)
                    .Select(p => p.ParentGroupId)
                    .ToListAsync(ct)
                    .ConfigureAwait(false)
                : await _db.UserGroupParents
                    .Where(p => p.ParentGroupId == current && p.IsActive)
                    .Select(p => p.ChildGroupId)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
            foreach (var id in nextIds)
            {
                if (visited.Add(id))
                {
                    orderedReachable.Add(id);
                    frontier.Enqueue(id);
                }
            }
        }

        if (orderedReachable.Count == 0)
        {
            return Array.Empty<UserGroupDto>();
        }

        var idSet = orderedReachable.ToHashSet();
        var groups = await _db.UserGroups
            .Where(g => idSet.Contains(g.Id) && g.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var groupsById = groups.ToDictionary(g => g.Id);

        var result = new List<UserGroupDto>(orderedReachable.Count);
        foreach (var id in orderedReachable)
        {
            if (!groupsById.TryGetValue(id, out var g))
            {
                continue;
            }
            result.Add(new UserGroupDto(
                Id: _sqids.Encode(g.Id),
                Code: g.Code,
                DisplayName: g.DisplayName,
                Description: g.Description,
                Kind: g.Kind.ToString(),
                Status: g.Status.ToString(),
                Roles: g.Roles.ToList(),
                DirectMemberCount: 0,
                DirectChildCount: 0,
                EffectiveRoleCount: 0));
        }
        return result;
    }
}
