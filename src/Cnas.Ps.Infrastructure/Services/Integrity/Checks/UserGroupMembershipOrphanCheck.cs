using Cnas.Ps.Application.Integrity;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Integrity.Checks;

/// <summary>
/// R2282 / TOR SEC 036 — invariant: every <see cref="UserGroupMembership"/>
/// row must reference a live (non-deleted) <see cref="UserGroup"/> AND a
/// live <see cref="UserProfile"/>. Orphan membership rows survive a parent
/// soft-delete and silently leak roles to disabled users — the resolver
/// walks them as if they were valid.
/// </summary>
public sealed class UserGroupMembershipOrphanCheck : IIntegrityCheck
{
    /// <inheritdoc />
    public string CheckCode => "USER_GROUP_MEMBERSHIP.ORPHAN";

    /// <inheritdoc />
    public string AggregateName => nameof(UserGroupMembership);

    /// <inheritdoc />
    public IntegrityFindingSeverity Severity => IntegrityFindingSeverity.Medium;

    /// <inheritdoc />
    public async Task<IntegrityCheckPartialResult> RunAsync(
        IIntegrityCheckContext ctx,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Pull live memberships + the set of live group ids + the set of
        // live user-profile ids. Set-based comparison keeps the join out of
        // SQL — the resolver dataset is small in practice.
        var memberships = await ctx.Db.UserGroupMemberships
            .Where(m => m.IsActive)
            .Select(m => new { m.Id, m.UserGroupId, m.UserProfileId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var liveGroupIds = await ctx.Db.UserGroups
            .Where(g => g.IsActive)
            .Select(g => g.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var liveGroupSet = liveGroupIds.ToHashSet();

        var liveUserIds = await ctx.Db.UserProfiles
            .Where(u => u.IsActive)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var liveUserSet = liveUserIds.ToHashSet();

        var findings = new List<IntegrityCheckFindingRecord>();
        foreach (var m in memberships)
        {
            var orphanGroup = !liveGroupSet.Contains(m.UserGroupId);
            var orphanUser = !liveUserSet.Contains(m.UserProfileId);
            if (orphanGroup || orphanUser)
            {
                var dangling = orphanGroup && orphanUser ? "both"
                    : orphanGroup ? "group"
                    : "user";
                findings.Add(new IntegrityCheckFindingRecord(
                    CheckCode: CheckCode,
                    Severity: Severity,
                    AggregateName: AggregateName,
                    AggregateRowId: m.Id,
                    Description: $"UserGroupMembership references {dangling} target(s) that are deleted or missing.",
                    ExpectedValue: "UserGroup AND UserProfile must both be live",
                    ActualValue: $"orphanGroup={orphanGroup}; orphanUser={orphanUser}; UserGroupId={m.UserGroupId}; UserProfileId={m.UserProfileId}"));
            }
        }

        return new IntegrityCheckPartialResult(memberships.Count, findings);
    }
}
