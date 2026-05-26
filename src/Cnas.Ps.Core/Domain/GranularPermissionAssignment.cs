using Cnas.Ps.Core.Audit;

namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0673 / TOR CF 18.12 — one row of the granular permission matrix. Maps a
/// triple <c>(RoleCode, ResourceType, PermissionVerb)</c> to a grant. Presence
/// of the row means "callers carrying <c>RoleCode</c> may perform
/// <c>PermissionVerb</c> on <c>ResourceType</c>"; absence means "denied".
/// </summary>
/// <remarks>
/// <para>
/// <b>Composition with role-based gates.</b> The coarse role policies in
/// <c>AuthorizationComposition</c> remain authoritative for endpoint-level
/// authentication (deny-by-default per CLAUDE.md §5.4); the granular matrix
/// layers a per-resource / per-verb gate ON TOP of the role check via the
/// <c>[GranularPermission("Resource", "Verb")]</c> attribute. The attribute
/// invokes the matrix as a defense-in-depth filter — if the role check would
/// already 403 the call, the matrix is never consulted.
/// </para>
/// <para>
/// <b>Uniqueness.</b> The
/// <c>(RoleCode, ResourceType, PermissionVerb)</c> triple is unique. Duplicate
/// assigns are idempotent — the service short-circuits to success without
/// emitting a new row.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> — the row is
/// surfaced to the admin REST surface through a Sqid so the admin UI can
/// reference an individual grant for revocation without exposing the raw long
/// primary key.
/// </para>
/// </remarks>
[AutoAudit(Severity = AuditSeverity.Notice, EventCodePrefix = "GRANULAR_PERM")]
public sealed class GranularPermissionAssignment : AuditableEntity, IExternalId
{
    /// <summary>
    /// Role code being granted permission. One of the stable
    /// <see cref="Cnas.Ps.Core.Common.RoleCodes"/> values
    /// (e.g. <c>cnas-user</c>, <c>cnas-decider</c>, <c>cnas-admin</c>) or a
    /// future generic-role string. Length ≤ 64.
    /// </summary>
    public string RoleCode { get; set; } = string.Empty;

    /// <summary>
    /// PascalCase resource discriminator (e.g. <c>Dossier</c>,
    /// <c>Document</c>, <c>Solicitant</c>). Free-form so new resources can be
    /// added without a Core change. Length ≤ 64.
    /// </summary>
    public string ResourceType { get; set; } = string.Empty;

    /// <summary>
    /// Permission verb. One of the stable
    /// <see cref="Cnas.Ps.Core.Common.PermissionVerbs"/> values
    /// (View / Add / Modify / StatusChange / Generate / Download). Length ≤ 32.
    /// </summary>
    public string PermissionVerb { get; set; } = string.Empty;

    /// <summary>UTC instant the grant was created.</summary>
    public DateTime GrantedAtUtc { get; set; }

    /// <summary>
    /// Database id of the administrator that issued the grant. Captured at
    /// the boundary from <c>ICallerContext.UserId</c>; <c>null</c> when the
    /// row was created by a system path (test fixture, migration seed).
    /// </summary>
    public long? GrantedByUserId { get; set; }
}
