using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R2274 / TOR SEC 028 — Access-rights "who can do what" report DTOs
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R2274 / TOR SEC 028 — paging / filter envelope used by the by-role and
/// full-matrix endpoints. <paramref name="Skip"/> is zero-based; <paramref name="Take"/>
/// is capped at 500 to keep the payload size bounded.
/// </summary>
/// <param name="Skip">Pagination skip count (≥ 0).</param>
/// <param name="Take">Pagination page size (1..500).</param>
/// <param name="IncludeDisabledAccounts">
/// When <c>true</c>, users whose <c>UserAccountState</c> is not
/// <c>Active</c> are included in the report rows; when <c>false</c>, only
/// active accounts are returned. The audit row records the flag value.
/// </param>
public sealed record AccessRightsReportPagingDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Take,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IncludeDisabledAccounts);

/// <summary>
/// R2274 — describes how a user gained a role: directly (assigned to the
/// <c>UserProfile.Roles</c> list) or inherited through one or more groups.
/// </summary>
public enum AccessRightsGrantKind
{
    /// <summary>The role is directly attached to the <c>UserProfile.Roles</c> list.</summary>
    Direct = 0,

    /// <summary>The role is inherited through one or more <c>UserGroup</c> memberships.</summary>
    Inherited = 1,

    /// <summary>The user is a direct member of the group whose subtree this report scans.</summary>
    DirectInGroup = 2,

    /// <summary>The user is a member of a descendant of the group whose subtree this report scans.</summary>
    InheritedFromDescendant = 3,
}

/// <summary>
/// R2274 — one effective role belonging to a user, with the chain of groups
/// that contributed it. An empty chain means the role is direct.
/// </summary>
/// <param name="RoleCode">The role-code held by the user.</param>
/// <param name="GrantKind">Direct vs. inherited (stable enum-name string).</param>
/// <param name="GrantingGroupChain">
/// Ordered chain of group codes that produced the grant (empty for direct
/// grants). The first entry is the user's direct group; subsequent entries
/// are the ancestors traversed in inheritance order.
/// </param>
public sealed record AccessRightsEffectiveRoleDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RoleCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string GrantKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string> GrantingGroupChain);

/// <summary>
/// R2274 — one (group code + display-name) pair surfaced in
/// <see cref="AccessRightsByUserReportDto.GroupMemberships"/>.
/// </summary>
/// <param name="GroupCode">Stable group code.</param>
/// <param name="GroupDisplayName">Human-friendly group name.</param>
public sealed record AccessRightsGroupMembershipDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string GroupCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string GroupDisplayName);

/// <summary>
/// R2274 — one row in the by-role / full-matrix reports.
/// </summary>
/// <param name="UserSqid">Sqid-encoded user-profile id.</param>
/// <param name="DisplayName">User's display name.</param>
/// <param name="Email">User's email (Internal sensitivity).</param>
/// <param name="AccountStatus">Stable enum-name string of <c>UserAccountState</c>.</param>
/// <param name="GrantKind">Stable enum-name string of <see cref="AccessRightsGrantKind"/>.</param>
/// <param name="GrantingGroups">
/// When <see cref="GrantKind"/> is <c>Inherited</c>, the set of group codes
/// that contribute the grant; empty for direct grants.
/// </param>
public sealed record UserAccessRowDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string UserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Email is citizen contact PII per R0228 / SEC 033 — matches ProfileOutput.Email.")]
    string? Email,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string AccountStatus,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string GrantKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string> GrantingGroups);

/// <summary>
/// R2274 — full access-rights snapshot for a single user. Returned by the
/// by-user endpoint.
/// </summary>
/// <param name="UserSqid">Sqid-encoded user-profile id.</param>
/// <param name="DisplayName">User's display name.</param>
/// <param name="Email">User's email (Internal sensitivity).</param>
/// <param name="AccountStatus">Stable enum-name string of <c>UserAccountState</c>.</param>
/// <param name="DirectRoles">Role codes directly assigned to <c>UserProfile.Roles</c>.</param>
/// <param name="EffectiveRoles">
/// Union of direct roles + roles inherited through transitive group
/// memberships. Each row records its grant kind and group chain.
/// </param>
/// <param name="GroupMemberships">
/// Direct <c>UserGroupMembership</c> rows projected to (code, display-name)
/// pairs.
/// </param>
public sealed record AccessRightsByUserReportDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string UserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Email is citizen contact PII per R0228 / SEC 033 — matches ProfileOutput.Email.")]
    string? Email,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string AccountStatus,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string> DirectRoles,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<AccessRightsEffectiveRoleDto> EffectiveRoles,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<AccessRightsGroupMembershipDto> GroupMemberships);

/// <summary>
/// R2274 — paged by-role report: every user (direct or via group) who
/// effectively holds the supplied role-code.
/// </summary>
/// <param name="RoleCode">The role-code the report was generated for.</param>
/// <param name="Items">Materialised page of access rows.</param>
/// <param name="Total">Total count BEFORE pagination.</param>
/// <param name="Skip">Echo of the request's skip parameter.</param>
/// <param name="Take">Echo of the request's take parameter.</param>
public sealed record AccessRightsByRoleReportDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RoleCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<UserAccessRowDto> Items,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Total,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Take);

/// <summary>
/// R2274 — one member row in the by-group report.
/// </summary>
/// <param name="UserSqid">Sqid-encoded user-profile id.</param>
/// <param name="DisplayName">User's display name.</param>
/// <param name="GrantKind">
/// Stable enum-name string of <see cref="AccessRightsGrantKind"/> —
/// <c>DirectInGroup</c> for users directly registered against the group, or
/// <c>InheritedFromDescendant</c> for users registered against a descendant.
/// </param>
/// <param name="SourceGroupCode">
/// Stable code of the group that actually carries the membership row
/// (equals the requested group for direct members, or one of the descendants
/// for inherited rows).
/// </param>
public sealed record AccessRightsByGroupMemberRowDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string UserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string GrantKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string SourceGroupCode);

/// <summary>
/// R2274 — by-group report: every user reachable through this group + its
/// descendants, plus the aggregated effective role-codes the subtree
/// contributes.
/// </summary>
/// <param name="GroupSqid">Sqid-encoded group id of the queried group.</param>
/// <param name="GroupCode">Stable code of the queried group.</param>
/// <param name="GroupDisplayName">Display name of the queried group.</param>
/// <param name="DescendantGroupCodes">
/// Ordered list of descendant group codes whose memberships are folded into
/// the report (excludes the queried group itself).
/// </param>
/// <param name="Members">
/// One row per distinct user reached through the subtree, recording the
/// source group that carries the membership.
/// </param>
/// <param name="AggregatedRoleCodes">
/// Distinct role codes contributed by the queried group AND every active
/// descendant. Disabled groups contribute no roles.
/// </param>
public sealed record AccessRightsByGroupReportDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string GroupSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string GroupCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string GroupDisplayName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string> DescendantGroupCodes,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<AccessRightsByGroupMemberRowDto> Members,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string> AggregatedRoleCodes);
