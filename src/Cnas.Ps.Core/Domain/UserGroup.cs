namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2270 / TOR SEC 023-024 — first-class user-group aggregate. Replaces the
/// loose <c>UserProfile.Groups</c> string list with a registered entity that
/// can be nested (forming a DAG) and that aggregates role grants for transitive
/// resolution.
/// </summary>
/// <remarks>
/// <para>
/// <b>Code stability.</b> <see cref="Code"/> is the stable external identifier
/// (e.g. <c>OFFICE_CHISINAU_CENTRU</c>). It is NOT a Sqid — it is a
/// human-readable domain code that the application surfaces verbatim in URLs,
/// audit payloads, and group-share metadata on <c>SavedSearch</c>. The
/// surrogate primary key is exposed externally only via Sqid encoding through
/// <see cref="IExternalId"/>.
/// </para>
/// <para>
/// <b>Nesting + cycle prevention.</b> A group may belong to one or more
/// parent groups via the <see cref="Parents"/> / <see cref="Children"/>
/// navigations (modelled through the <c>UserGroupParent</c> join entity). The
/// service layer rejects any add-child request that would create a cycle —
/// the read-side resolver performs a defence-in-depth check anyway and emits
/// a Critical <c>USER_GROUP.CYCLE_DETECTED</c> audit row if one slips through.
/// </para>
/// <para>
/// <b>Role aggregation.</b> The direct <see cref="Roles"/> list is persisted
/// as a <c>text[]</c> column on PostgreSQL (mirrors the
/// <c>UserProfile.Roles</c> shape). Transitive roles are computed at lookup
/// time by <c>IUserGroupRoleResolver</c> — they are NOT cached on the entity.
/// </para>
/// </remarks>
public sealed class UserGroup : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable external code (e.g. <c>OFFICE_CHISINAU_CENTRU</c>). Required,
    /// max length 64, must match the regex <c>^[A-Z][A-Z0-9_]{1,63}$</c>.
    /// Validated at the service boundary; the unique-index on this column
    /// enforces system-wide uniqueness.
    /// </summary>
    public required string Code { get; set; }

    /// <summary>Human-friendly name surfaced in UI / pickers (3..256 chars).</summary>
    public required string DisplayName { get; set; }

    /// <summary>Optional free-form description of the group's purpose (≤ 1000 chars).</summary>
    public string? Description { get; set; }

    /// <summary>Classification driving the UI grouping and audit bucketing.</summary>
    public UserGroupKind Kind { get; set; } = UserGroupKind.Custom;

    /// <summary>
    /// Lifecycle state of the group. Disabled groups do not contribute roles
    /// to the transitive resolution performed by <c>IUserGroupRoleResolver</c>.
    /// </summary>
    public UserGroupStatus Status { get; set; } = UserGroupStatus.Active;

    /// <summary>
    /// Direct role-code grants. The transitive resolver unions this list with
    /// every ancestor group's <see cref="Roles"/>.
    /// </summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>Reverse navigation — groups this row participates in as a CHILD.</summary>
    /// <remarks>Each row records that this group is nested INSIDE another.</remarks>
    public ICollection<UserGroupParent> Parents { get; set; } = [];

    /// <summary>Forward navigation — groups that have this group as their parent.</summary>
    /// <remarks>Each row records that another group is nested INSIDE this one.</remarks>
    public ICollection<UserGroupParent> Children { get; set; } = [];

    /// <summary>Direct user memberships in this group.</summary>
    public ICollection<UserGroupMembership> Memberships { get; set; } = [];
}

/// <summary>
/// R2270 / TOR SEC 023-024 — join row recording that one user-group is a
/// member of another. Together with <see cref="UserGroup"/> these rows form
/// a directed acyclic graph (DAG): the parent's role grants flow down to
/// every transitive child via <c>IUserGroupRoleResolver</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composite primary key.</b> <c>(ParentGroupId, ChildGroupId)</c>. The
/// service layer rejects any request where the two ids are equal (self-loop)
/// AND any request where the proposed child already appears as an ancestor
/// of the proposed parent (cycle).
/// </para>
/// <para>
/// <b>Audit attribution.</b> Inherits <see cref="AuditableEntity"/> so the
/// "who linked this group + when" pair is captured alongside the lifecycle
/// metadata. The surrogate <c>Id</c> column is never exposed externally —
/// the join is identified by the <c>(parent, child)</c> pair.
/// </para>
/// </remarks>
public sealed class UserGroupParent : AuditableEntity
{
    /// <summary>FK to the parent <see cref="UserGroup"/> (the role-granting group).</summary>
    public long ParentGroupId { get; set; }

    /// <summary>Navigation to the parent group.</summary>
    public UserGroup? ParentGroup { get; set; }

    /// <summary>FK to the child <see cref="UserGroup"/> (the role-inheriting group).</summary>
    public long ChildGroupId { get; set; }

    /// <summary>Navigation to the child group.</summary>
    public UserGroup? ChildGroup { get; set; }
}

/// <summary>
/// R2270 / TOR SEC 023-024 — join row recording that a <see cref="UserProfile"/>
/// is a direct member of a <see cref="UserGroup"/>. The transitive role
/// resolver walks upward from these rows to union every ancestor group's
/// role grants.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composite primary key.</b> <c>(UserGroupId, UserProfileId)</c>. Inserts
/// are idempotent — repeated membership requests succeed silently.
/// </para>
/// <para>
/// <b>Distinct from <see cref="UserProfile.Groups"/>.</b> The legacy
/// <see cref="UserProfile.Groups"/> list of group codes is retained for
/// backwards compatibility with <c>SavedSearch.SharedWithGroupCode</c>; the
/// authoritative source for role-resolution membership is the row set in
/// this table.
/// </para>
/// </remarks>
public sealed class UserGroupMembership : AuditableEntity
{
    /// <summary>FK to the parent <see cref="UserGroup"/>.</summary>
    public long UserGroupId { get; set; }

    /// <summary>Navigation to the group.</summary>
    public UserGroup? UserGroup { get; set; }

    /// <summary>FK to the <see cref="UserProfile"/> that is a member.</summary>
    public long UserProfileId { get; set; }

    /// <summary>Navigation to the user profile.</summary>
    public UserProfile? UserProfile { get; set; }
}
