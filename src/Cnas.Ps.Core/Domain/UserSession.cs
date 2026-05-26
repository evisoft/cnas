using Cnas.Ps.Core.Audit;

namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2264 / SEC 017 + R2267 / SEC 020 — one row per authenticated user session on the
/// CNAS portal. Inserted whenever the auth pipeline mints a fresh access token and
/// drives two security primitives:
/// <list type="bullet">
///   <item>
///     <description>
///       <b>Concurrent-session limit (R2264).</b> The
///       <c>ISessionLimitEnforcer</c> counts non-terminated rows per user and
///       force-terminates the oldest when the configured ceiling
///       (default 3) would be exceeded.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Session lock (R2267).</b> The user-facing
///       <c>/api/profile/lock-session</c> endpoint plus the
///       <c>SessionAutoLockJob</c> idle-sweep flip
///       <see cref="IsLocked"/> to <c>true</c> so middleware can refuse
///       further requests until the user re-authenticates.
///     </description>
///   </item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>SessionId payload.</b> <see cref="SessionId"/> is an opaque token — typically
/// the JWT <c>jti</c> claim or the refresh-token family id — bound to a single
/// authentication event. It is NEVER the bearer token itself; the row at rest
/// therefore carries no credential material that would compromise the user if the
/// table were exfiltrated. The unique index on this column enforces "one row per
/// session" and accelerates the middleware look-up path.
/// </para>
/// <para>
/// <b>PII discipline.</b> Only <see cref="IpAddress"/> and <see cref="UserAgent"/>
/// (both useful for forensic reconstruction) are stored alongside the user-id
/// foreign key; no IDNP / email / national id ever lands on this row. The user-id is
/// the raw <c>bigint</c> per the layer convention; Sqid encoding happens at the API
/// boundary via <see cref="IExternalId"/>.
/// </para>
/// <para>
/// <b>Lifecycle.</b>
/// <c>Created (IsLocked=false, IsTerminated=false)</c> →
/// <c>Locked (IsLocked=true)</c> [manual or idle auto-lock] →
/// <c>Terminated (IsTerminated=true)</c> [logout, concurrent-limit eviction, or
/// admin force-termination]. Termination is irreversible — the table is append-only
/// in the sense that no row ever transitions back to <c>IsTerminated=false</c>.
/// </para>
/// </remarks>
[AutoAudit(Severity = AuditSeverity.Notice, EventCodePrefix = "SESSION")]
public sealed class UserSession : AuditableEntity, IExternalId
{
    /// <summary>
    /// Foreign key to <see cref="UserProfile"/> — the session owner. Indexed alongside
    /// <see cref="IsTerminated"/> and <see cref="AuditableEntity.CreatedAtUtc"/> to
    /// make the "currently active sessions for this user" probe a single
    /// composite-index seek.
    /// </summary>
    public long UserUserId { get; set; }

    /// <summary>
    /// Opaque session identifier — typically the JWT <c>jti</c> claim or the refresh-
    /// token family id. Unique across the table; lookup by this column is the hot
    /// path consulted by <c>ISessionLockService</c> middleware on every authenticated
    /// request, so it MUST stay backed by an index. Capped at 128 chars at the column
    /// layer to keep B-tree page density reasonable.
    /// </summary>
    public required string SessionId { get; set; }

    /// <summary>
    /// Source IP of the inbound request that minted this session. Optional because
    /// dev / E2E fixtures may not always have a non-null remote address. Stored
    /// verbatim — IPv4 dotted-quad or IPv6 colon-hex; not parsed.
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// User-Agent string captured at session creation. Optional and capped at 512
    /// chars at the column layer; truncated silently if the inbound header exceeds the
    /// limit. Useful for forensic reconstruction ("this row came from Chrome 124 on
    /// Windows") without compromising user privacy.
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// UTC instant the last request bound to this session was observed by the auth
    /// pipeline. Bumped by middleware on every authenticated request; consulted by
    /// the <c>SessionAutoLockJob</c> to identify idle sessions past the configured
    /// 15-minute threshold (R2265 / R2267).
    /// </summary>
    public DateTime LastActivityUtc { get; set; }

    /// <summary>
    /// <c>true</c> when the user has explicitly locked the session via
    /// <c>POST /api/profile/lock-session</c> OR the
    /// <c>SessionAutoLockJob</c> has flipped the flag due to idle past
    /// <c>SessionLimitOptions.IdleLockMinutes</c>. A locked session is refused further
    /// requests by <c>SessionLockMiddleware</c> until the user re-authenticates.
    /// </summary>
    public bool IsLocked { get; set; }

    /// <summary>
    /// UTC instant <see cref="IsLocked"/> was flipped to <c>true</c>, or <c>null</c>
    /// while the session remains unlocked. Pinned to
    /// <c>ICnasTimeProvider.UtcNow</c> at flip time — never <c>DateTime.UtcNow</c>.
    /// </summary>
    public DateTime? LockedAtUtc { get; set; }

    /// <summary>
    /// <c>true</c> when the session has been definitively terminated (logout,
    /// concurrent-session-limit eviction, or admin force-terminate). Once true, the
    /// row never transitions back — the limit-enforcer and lock service both filter
    /// on this flag to exclude already-dead sessions from the "active" working set.
    /// </summary>
    public bool IsTerminated { get; set; }

    /// <summary>
    /// UTC instant <see cref="IsTerminated"/> was flipped to <c>true</c>, or
    /// <c>null</c> while the session remains live. Pinned to
    /// <c>ICnasTimeProvider.UtcNow</c> at flip time.
    /// </summary>
    public DateTime? TerminatedAtUtc { get; set; }

    /// <summary>
    /// Short stable termination reason — e.g.
    /// <c>"Logout"</c>, <c>"ConcurrentLimitExceeded"</c>,
    /// <c>"AdminForceTerminate"</c>. Capped at 64 chars at the column layer;
    /// intended for ops dashboards and forensic queries, never for end-user display.
    /// </summary>
    public string? TerminationReason { get; set; }
}
