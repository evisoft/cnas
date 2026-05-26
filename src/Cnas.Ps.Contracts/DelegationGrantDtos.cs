namespace Cnas.Ps.Contracts;

/// <summary>
/// R0057 / TOR SEC 026 + CF 16.11 — output projection of one delegation grant. All
/// identifiers are Sqid-encoded per CLAUDE.md RULE 3; the grantor's / delegatee's
/// identity surfaces as the Sqid form of their <c>UserProfile.Id</c> only.
/// </summary>
/// <param name="Id">Sqid-encoded id of the grant row.</param>
/// <param name="GrantorUserId">Sqid-encoded <c>UserProfile.Id</c> of the user that issued the grant.</param>
/// <param name="DelegateeUserId">Sqid-encoded <c>UserProfile.Id</c> of the user receiving the rights.</param>
/// <param name="ValidFromUtc">Inclusive UTC start of the delegation window.</param>
/// <param name="ValidToUtc">Exclusive UTC end of the delegation window.</param>
/// <param name="SuspendsGrantorRights">
/// When <c>true</c>, the grantor's own rights covered by <see cref="Scope"/> are
/// suspended for the duration of the window — only the delegatee may exercise them.
/// </param>
/// <param name="Scope">Free-text scope discriminator (e.g. <c>"approve.executory_documents"</c>).</param>
/// <param name="GrantedAtUtc">UTC instant at which the grant was issued.</param>
/// <param name="RevokedAtUtc">UTC instant at which the grant was revoked early, or <c>null</c>.</param>
/// <param name="RevokeReason">Free-form reason captured at revocation time, or <c>null</c>.</param>
public sealed record DelegationGrantDto(
    string Id,
    string GrantorUserId,
    string DelegateeUserId,
    DateTime ValidFromUtc,
    DateTime ValidToUtc,
    bool SuspendsGrantorRights,
    string Scope,
    DateTime GrantedAtUtc,
    DateTime? RevokedAtUtc,
    string? RevokeReason);

/// <summary>
/// R0057 / TOR SEC 026 + CF 16.11 — input body for <c>POST /api/delegations</c>. The
/// grantor is the calling user (derived from <c>ICallerContext.UserId</c>); the
/// delegatee is referenced by Sqid.
/// </summary>
/// <param name="DelegateeSqid">Sqid-encoded id of the delegatee user. MUST differ from the calling user.</param>
/// <param name="ValidFromUtc">Inclusive UTC start of the delegation window.</param>
/// <param name="ValidToUtc">Exclusive UTC end of the delegation window. MUST be &gt; ValidFromUtc and ≤ 90 days after it.</param>
/// <param name="SuspendsGrantorRights">Whether to suspend the grantor's own rights covered by <see cref="Scope"/>.</param>
/// <param name="Scope">Free-text scope discriminator (1..128 chars).</param>
public sealed record DelegationGrantInputDto(
    string DelegateeSqid,
    DateTime ValidFromUtc,
    DateTime ValidToUtc,
    bool SuspendsGrantorRights,
    string Scope);

/// <summary>
/// R0057 / TOR SEC 026 + CF 16.11 — input body for
/// <c>DELETE /api/delegations/{sqid}</c>. Carries the free-form revocation reason that
/// the service persists on the grant row and writes to the audit trail.
/// </summary>
/// <param name="Reason">Free-form revocation reason; 3..500 chars per validator.</param>
public sealed record DelegationGrantRevokeInputDto(string Reason);
