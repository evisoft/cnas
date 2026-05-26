namespace Cnas.Ps.Core.Domain;

/// <summary>
/// One row per opaque refresh token issued by the R0053 token pipeline
/// (CLAUDE.md §5.3 / TOR SEC 018). Lives behind <c>POST /api/auth/token</c> +
/// <c>POST /api/auth/logout</c> and underpins the rotation + reuse-detection
/// guarantees: every successful refresh consumes the presented token and inserts a
/// fresh child row; re-presenting a consumed token is treated as a stolen credential
/// and immediately revokes every live row in the family.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hash, not plaintext.</b> The plaintext refresh token is returned to the caller
/// exactly once (in the issue or rotate HTTP response) and NEVER persisted; the row
/// stores only <see cref="TokenHash"/> = SHA-256 hex of the plaintext. A database
/// compromise therefore does not yield usable refresh tokens — the attacker would
/// need to brute-force preimages, infeasible for a 48-byte random secret.
/// </para>
/// <para>
/// <b>Family chain.</b> Every login event mints a new <see cref="FamilyId"/>
/// (<see cref="System.Guid.NewGuid"/>); each subsequent rotation re-uses it.
/// <see cref="ParentTokenId"/> walks back to the family root and is <c>null</c> for
/// the root row. A logout or reuse-detected event flips <see cref="RevokedAtUtc"/>
/// on EVERY row sharing the family id — the attacker's stolen token and the
/// legitimate user's token are killed together so neither side can keep the family
/// alive.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because admin/audit
/// surfaces may eventually list per-user sessions via Sqid-encoded ids. The raw
/// <see cref="AuditableEntity.Id"/> never leaves the system through any DTO.
/// </para>
/// <para>
/// <b>PII discipline.</b> No PII is stored on this row — only the hash, the family
/// id (random GUID), the parent id, the foreign-key user id, and the timestamps.
/// Reuse-detection log lines emit the family id + numeric user id only; the IDNP,
/// email, and any other personally-identifying field stay out of the audit trail.
/// </para>
/// </remarks>
public sealed class RefreshToken : AuditableEntity, IExternalId
{
    /// <summary>
    /// SHA-256 hash of the opaque token string (lowercase hex, 64 chars). The plaintext
    /// is returned to the caller exactly once and NEVER persisted; storing only the
    /// hash means a DB compromise does not yield usable refresh tokens.
    /// </summary>
    public required string TokenHash { get; set; }

    /// <summary>
    /// Family id — the same value across every token in a single login's rotation
    /// chain. Minted (<see cref="System.Guid.NewGuid"/>) on <c>IssueAsync</c> and re-used
    /// on every <c>RotateAsync</c>. Logout / reuse-detection revoke every row sharing
    /// this id together.
    /// </summary>
    public Guid FamilyId { get; set; }

    /// <summary>
    /// The id of the token that produced this one via rotation, or <c>null</c> for the
    /// root row of a family. Enables backtracking the chain for audit and forensic
    /// reconstruction without scanning the whole family.
    /// </summary>
    public long? ParentTokenId { get; set; }

    /// <summary>
    /// Foreign key to <see cref="UserProfile"/>. Stored as the raw bigint per the layer's
    /// convention (internal code uses raw ids; Sqid encoding happens at the API
    /// boundary). Indexed to support "list this user's active sessions" queries.
    /// </summary>
    public long UserId { get; set; }

    /// <summary>UTC instant the token was issued. Pinned to <c>ICnasTimeProvider.UtcNow</c> at issue time.</summary>
    public DateTime IssuedAtUtc { get; set; }

    /// <summary>
    /// UTC instant the token expires. Defaults to <see cref="IssuedAtUtc"/> +
    /// <c>JwtOptions.RefreshTokenLifetime</c> (30 days per SEC 018).
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// UTC instant this token was consumed by a successful rotation. <c>null</c> while
    /// the token is still live. A non-null value combined with a subsequent rotation
    /// attempt is the signal that fires reuse-detection.
    /// </summary>
    public DateTime? ConsumedAtUtc { get; set; }

    /// <summary>
    /// UTC instant this token was revoked (logout, family-compromise, admin revoke).
    /// <c>null</c> while the token is still live. Revocation is permanent — there is
    /// no "un-revoke" path.
    /// </summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>
    /// Short stable reason for revocation (e.g. <c>"logout"</c>, <c>"reuse-detected"</c>,
    /// <c>"admin-revoke"</c>, <c>"account-not-active"</c>). <c>null</c> while the token
    /// is still live. Capped at 64 chars at the database layer; intended for ops
    /// dashboards and forensic queries, never for end-user display.
    /// </summary>
    public string? RevokedReason { get; set; }
}
