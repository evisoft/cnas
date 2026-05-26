using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0673 / TOR CF 18.12 — wire DTO for one granular permission assignment.
/// Sqid-encoded primary key per CLAUDE.md RULE 3; the three discriminator
/// fields (RoleCode / ResourceType / PermissionVerb) cross the boundary as
/// stable strings so admin tooling can reference them without an extra
/// classifier lookup.
/// </summary>
/// <param name="Id">Sqid-encoded id of the assignment row.</param>
/// <param name="RoleCode">Role code that carries the grant.</param>
/// <param name="ResourceType">PascalCase resource discriminator.</param>
/// <param name="PermissionVerb">Verb name (View / Add / Modify / StatusChange / Generate / Download).</param>
/// <param name="GrantedAtUtc">UTC instant the grant was created.</param>
/// <param name="GrantedByUserSqid">Sqid of the administrator that issued the grant; <c>null</c> for system-seeded rows.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record GranularPermissionAssignmentDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RoleCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ResourceType,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string PermissionVerb,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime GrantedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? GrantedByUserSqid);

/// <summary>
/// R0673 / TOR CF 18.12 — request body for
/// <c>POST /api/admin/permissions</c>. The grantor is the authenticated caller
/// — the DTO deliberately does NOT carry the grantor sqid so a non-admin
/// caller cannot forge an assignment attributed to someone else.
/// </summary>
/// <param name="RoleCode">Role code (from <c>RoleCodes</c>).</param>
/// <param name="ResourceType">PascalCase resource discriminator.</param>
/// <param name="PermissionVerb">Verb name (from <c>PermissionVerbs</c>).</param>
public sealed record GranularPermissionAssignInput(
    string RoleCode,
    string ResourceType,
    string PermissionVerb);
