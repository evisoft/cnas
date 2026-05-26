using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R2270 / TOR SEC 023-024 — UserGroup registry DTOs
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R2270 — one user-group row as it leaves the system. <paramref name="Code"/>
/// stays as a plain string — it is the stable domain code, NOT a Sqid. Only
/// the surrogate <paramref name="Id"/> is Sqid-encoded per CLAUDE.md RULE 3.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying row.</param>
/// <param name="Code">Stable external code (e.g. <c>OFFICE_CHISINAU_CENTRU</c>).</param>
/// <param name="DisplayName">Human-friendly name surfaced in UI / pickers.</param>
/// <param name="Description">Optional free-form description of the group's purpose.</param>
/// <param name="Kind">Stable enum-name of <c>UserGroupKind</c> (<c>OrganizationalUnit</c>, <c>FunctionalTeam</c>, <c>Project</c>, <c>Custom</c>).</param>
/// <param name="Status">Stable enum-name of <c>UserGroupStatus</c> (<c>Active</c>, <c>Disabled</c>).</param>
/// <param name="Roles">Direct role-code grants attached to this group.</param>
/// <param name="DirectMemberCount">Count of direct <c>UserGroupMembership</c> rows referencing this group.</param>
/// <param name="DirectChildCount">Count of direct child groups (rows where this group is the parent).</param>
/// <param name="EffectiveRoleCount">Distinct role count after transitive role resolution (direct + ancestors).</param>
public sealed record UserGroupDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Code,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Kind,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string> Roles,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int DirectMemberCount,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int DirectChildCount,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int EffectiveRoleCount);

/// <summary>
/// R2270 — input DTO for the <c>POST /api/user-groups</c> endpoint.
/// </summary>
/// <param name="Code">Stable external code, must match <c>^[A-Z][A-Z0-9_]{1,63}$</c>.</param>
/// <param name="DisplayName">Display name (3..256 chars).</param>
/// <param name="Description">Optional description (≤ 1000 chars).</param>
/// <param name="Kind">Stable enum-name of <c>UserGroupKind</c>.</param>
/// <param name="Roles">Initial direct role grants (each matching <c>^[A-Z][A-Z0-9_]{1,63}$</c>; ≤ 50 items).</param>
public sealed record UserGroupCreateInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Code,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Kind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string> Roles);

/// <summary>
/// R2270 — input DTO for the <c>PUT /api/user-groups/{sqid}</c> endpoint.
/// Nullable fields = "leave unchanged". <paramref name="ChangeReason"/> is
/// mandatory (3..500 chars).
/// </summary>
/// <param name="DisplayName">New display name; null leaves unchanged.</param>
/// <param name="Description">New description; null leaves unchanged.</param>
/// <param name="Kind">New kind (stable enum-name); null leaves unchanged.</param>
/// <param name="Roles">New direct role list (replaces previous); null leaves unchanged.</param>
/// <param name="ChangeReason">Mandatory operator-supplied rationale (3..500 chars).</param>
public sealed record UserGroupModifyInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Kind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string>? Roles,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ChangeReason);

/// <summary>
/// R2270 — input DTO for the disable / enable / delete / add-child /
/// remove-child / add-member / remove-member endpoints. Carries only the
/// operator-supplied rationale.
/// </summary>
/// <param name="Reason">Operator-supplied rationale (3..500 chars).</param>
public sealed record UserGroupReasonInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);

/// <summary>
/// R2270 — query envelope for the <c>GET /api/user-groups</c> list endpoint.
/// All fields are optional — omitted = "no filter".
/// </summary>
/// <param name="Status">Optional status filter (<c>Active</c> / <c>Disabled</c>).</param>
/// <param name="Kind">Optional kind filter.</param>
/// <param name="RoleCode">Optional role code — returns only groups that grant this role directly OR via inheritance.</param>
/// <param name="Skip">Pagination skip count (≥ 0).</param>
/// <param name="Take">Pagination page size (1..200).</param>
public sealed record UserGroupListFilterDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Kind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? RoleCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Take);

/// <summary>
/// R2270 — paged response shape for the list endpoint.
/// </summary>
/// <param name="Items">Materialised page of group rows.</param>
/// <param name="Total">Total count BEFORE pagination — used by the UI to render the page count.</param>
/// <param name="Skip">Echo of the request's skip parameter.</param>
/// <param name="Take">Echo of the request's take parameter.</param>
public sealed record UserGroupListPageDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<UserGroupDto> Items,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Total,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Take);

/// <summary>
/// R2270 — effective-roles resolution envelope returned by
/// <c>GET /api/user-groups/users/{userSqid}/effective-roles</c>.
/// </summary>
/// <param name="UserSqid">Sqid-encoded id of the user the roles were resolved for.</param>
/// <param name="Roles">One entry per distinct role, each carrying the chain of group codes that contributed it.</param>
public sealed record UserGroupEffectiveRolesDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string UserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<UserGroupEffectiveRoleDto> Roles);

/// <summary>
/// R2270 — one resolved role inside <see cref="UserGroupEffectiveRolesDto"/>.
/// </summary>
/// <param name="RoleCode">Role code the user transitively holds.</param>
/// <param name="GrantingGroupChain">Ordered chain of group codes that produced the grant — the first entry is the directly-granting group, subsequent entries are the ancestors traversed in inheritance order.</param>
public sealed record UserGroupEffectiveRoleDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RoleCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string> GrantingGroupChain);
