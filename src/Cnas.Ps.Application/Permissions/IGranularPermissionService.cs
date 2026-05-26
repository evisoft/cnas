using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Permissions;

/// <summary>
/// R0673 / TOR CF 18.12 — application contract for the granular permission
/// matrix that complements the coarse role-based gates declared in
/// <c>AuthorizationComposition</c>. The service exposes CRUD admin operations
/// against the
/// <see cref="Cnas.Ps.Core.Domain.GranularPermissionAssignment"/> table plus a
/// hot-path <see cref="HasPermissionAsync"/> probe consulted by the
/// <c>[GranularPermission("Resource","Verb")]</c> action filter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stable error codes.</b>
/// <see cref="ErrorCodes.GranularPermissionUnknownRole"/> — the supplied role
/// code is not in <see cref="RoleCodes.All"/>;
/// <see cref="ErrorCodes.GranularPermissionUnknownVerb"/> — the supplied verb
/// is not in <see cref="PermissionVerbs.All"/>;
/// <see cref="ErrorCodes.NotFound"/> — the targeted assignment Sqid does not
/// resolve to a live row;
/// <see cref="ErrorCodes.Forbidden"/> — the caller is not in
/// <c>cnas-admin</c>;
/// <see cref="ErrorCodes.Unauthorized"/> — the caller is anonymous.
/// </para>
/// <para>
/// <b>Idempotency.</b> <see cref="AssignAsync"/> returns success when the
/// triple is already granted; the service does NOT emit a new row. Revokes
/// against an already-revoked row return <see cref="ErrorCodes.NotFound"/> so
/// the admin UI can render an "already revoked" hint.
/// </para>
/// </remarks>
public interface IGranularPermissionService
{
    /// <summary>
    /// Grants <paramref name="permissionVerb"/> on <paramref name="resourceType"/>
    /// to every caller carrying <paramref name="roleCode"/>. Admin-only.
    /// Idempotent — duplicate assigns short-circuit to success.
    /// </summary>
    /// <param name="roleCode">Role code from <see cref="RoleCodes"/>.</param>
    /// <param name="resourceType">PascalCase resource discriminator.</param>
    /// <param name="permissionVerb">Verb from <see cref="PermissionVerbs"/>.</param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>Success or a failure with a stable code.</returns>
    Task<Result<GranularPermissionAssignmentDto>> AssignAsync(
        string roleCode,
        string resourceType,
        string permissionVerb,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes the assignment by its Sqid. Admin-only.
    /// </summary>
    /// <param name="assignmentSqid">Sqid-encoded id of the assignment row.</param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>Success or a failure with a stable code.</returns>
    Task<Result> RevokeAsync(string assignmentSqid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> when the (role, resource, verb) triple has an
    /// active grant. Consulted on the hot path by the
    /// <c>GranularPermissionFilter</c>; no authentication / authorization
    /// of the caller is performed — the filter does that gating itself.
    /// </summary>
    /// <param name="roleCode">Role code to probe.</param>
    /// <param name="resourceType">Resource discriminator.</param>
    /// <param name="permissionVerb">Verb to probe.</param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>
    /// <see cref="Result{T}.Success"/> wrapping <c>true</c> when the grant
    /// exists, <c>false</c> otherwise. Unknown roles / verbs return
    /// <c>false</c> (deny-by-default) — they never trip a failure code on
    /// this hot-path probe so the action filter can fast-deny without
    /// branching on the failure shape.
    /// </returns>
    Task<Result<bool>> HasPermissionAsync(
        string roleCode,
        string resourceType,
        string permissionVerb,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every active grant. Admin-only.
    /// </summary>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>The accessible grants; an empty list when none exist.</returns>
    Task<Result<IReadOnlyList<GranularPermissionAssignmentDto>>> ListAsync(
        CancellationToken cancellationToken = default);
}
