using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Identity;

/// <summary>
/// R2270 / TOR SEC 023-024 — read-side resolver for transitive group role
/// inheritance. Walks the <c>UserGroupParent</c> DAG to union every ancestor
/// group's <c>Roles</c> grant onto the user's direct membership set.
/// </summary>
/// <remarks>
/// <para>
/// <b>Defence in depth.</b> The write-side service already rejects cycles —
/// the resolver detects any cycle that slips through at read time and emits
/// a Critical <c>USER_GROUP.CYCLE_DETECTED</c> audit row plus an OTel
/// counter increment. The resolver itself short-circuits the traversal so
/// it never spins.
/// </para>
/// <para>
/// <b>Disabled groups.</b> A group whose
/// <see cref="Cnas.Ps.Core.Domain.UserGroup.Status"/> is
/// <see cref="Cnas.Ps.Core.Domain.UserGroupStatus.Disabled"/> contributes NO
/// roles, but the traversal still walks through it so a disabled
/// intermediary does not silently hide a re-enabled ancestor.
/// </para>
/// </remarks>
public interface IUserGroupRoleResolver
{
    /// <summary>
    /// Resolves every role-code the user transitively inherits through their
    /// direct group memberships.
    /// </summary>
    /// <param name="userProfileId">Internal numeric user-profile id.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success a populated envelope with one row per distinct role; an
    /// empty list when the user has no memberships.
    /// </returns>
    Task<Result<UserGroupEffectiveRolesDto>> ResolveEffectiveRolesAsync(
        long userProfileId,
        CancellationToken ct = default);

    /// <summary>
    /// Walks the parent DAG upward from <paramref name="groupId"/> and returns
    /// every transitive ancestor group, ordered by traversal depth (closest
    /// ancestor first).
    /// </summary>
    /// <param name="groupId">Internal numeric group id.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Ordered ancestor list; empty when the group has no parents.</returns>
    Task<Result<IReadOnlyList<UserGroupDto>>> ResolveAncestorsAsync(
        long groupId,
        CancellationToken ct = default);

    /// <summary>
    /// Walks the parent DAG downward from <paramref name="groupId"/> and returns
    /// every transitive descendant group, ordered by traversal depth.
    /// </summary>
    /// <param name="groupId">Internal numeric group id.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Ordered descendant list; empty when the group has no children.</returns>
    Task<Result<IReadOnlyList<UserGroupDto>>> ResolveDescendantsAsync(
        long groupId,
        CancellationToken ct = default);
}
