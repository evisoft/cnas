namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// Carries information about the currently-authenticated caller through the service layer.
/// Provided by the API/Web layers from the HTTP context (Sqid-decoded id, roles, IP).
/// </summary>
public interface ICallerContext
{
    /// <summary>Internal user primary key of the calling user, or null when anonymous.</summary>
    long? UserId { get; }

    /// <summary>Sqid id used in tokens/links — kept for emitting on audit log entries.</summary>
    string? UserSqid { get; }

    /// <summary>Set of granted role codes; empty when anonymous.</summary>
    IReadOnlyCollection<string> Roles { get; }

    /// <summary>Source IP of the inbound request, when known.</summary>
    string? SourceIp { get; }

    /// <summary>Correlation id of the inbound request.</summary>
    string? CorrelationId { get; }

    /// <summary>
    /// IDNP of the principal the caller is acting on behalf of, or null if the caller
    /// is acting in their own capacity. Populated from the MPass SAML
    /// <c>MPower:PrincipalIdnp</c> attribute at sign-in by <c>UserDirectoryService</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MPower is consumed indirectly via MPass — when a citizen authorises a delegate
    /// through the MPower portal and that delegate later signs into the CNAS portal
    /// through MPass, the SAML assertion carries the principal's IDNP plus an opaque
    /// delegation id. The application service compares this value to
    /// <c>SubmitApplicationInput.OnBehalfOfPrincipalIdnp</c> to decide whether the
    /// requested representation is actually authorised (UC06 CF 06.02, R0551).
    /// </para>
    /// <para>
    /// Returns <c>null</c> for system / background callers (jobs, internal services)
    /// and for normal interactive callers who are NOT acting on someone else's behalf.
    /// </para>
    /// </remarks>
    string? OnBehalfOfPrincipalIdnp { get; }

    /// <summary>
    /// Opaque MPower delegation identifier carried by the SAML assertion (or null).
    /// Used for audit traceability — the citizen's MPower record can be looked up in
    /// MPass / MPower portal by this id when reconciling.
    /// </summary>
    /// <remarks>
    /// Populated from the MPass SAML <c>MPower:DelegationId</c> attribute. Always
    /// captured alongside <see cref="OnBehalfOfPrincipalIdnp"/> on audit log entries
    /// so a future investigator can correlate a CNAS-side dossier to the underlying
    /// power-of-attorney record on the MPower side. Returns <c>null</c> when the
    /// caller is not acting via a delegation.
    /// </remarks>
    string? DelegationPowerId { get; }

    /// <summary>
    /// R0671 / TOR CF 18.06 — granular row-level access-scope envelope derived from
    /// the caller's roles + groups. Composed by the
    /// <c>Cnas.Ps.Infrastructure.AccessScope.RolesBasedAccessScope</c> at request
    /// time and consumed by every list-style service through
    /// <c>Cnas.Ps.Application.AccessScope.IAccessScopeFilter</c>. Never <c>null</c> —
    /// anonymous callers receive an unscoped envelope (the API-level
    /// <c>[Authorize]</c> gates anonymous access well before any service-layer
    /// consumer reaches this property).
    /// </summary>
    IAccessScope AccessScope { get; }

    /// <summary>
    /// R2264 / SEC 017 + R2267 / SEC 020 — opaque session identifier of the inbound
    /// request, typically the JWT <c>jti</c> claim or the refresh-token family id.
    /// <c>null</c> for anonymous callers or service-to-service paths that do not
    /// participate in the user-session lifecycle. Consumed by
    /// <c>ISessionLockService</c> + <c>ISessionLimitEnforcer</c> when the user-
    /// facing <c>/api/profile/lock-session</c> endpoint resolves the caller's row.
    /// </summary>
    string? SessionId { get; }
}
