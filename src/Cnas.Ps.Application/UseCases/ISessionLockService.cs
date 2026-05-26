using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R2267 / SEC 020 — drives the manual + auto session-lock primitives. A locked
/// session refuses further requests until the user re-authenticates; the lock flag
/// is consumed by <c>SessionLockMiddleware</c> on every authenticated request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Auto-lock pipeline.</b> The companion <c>SessionAutoLockJob</c> sweeps the
/// table every 5 minutes for sessions whose <c>LastActivityUtc</c> is older than
/// <see cref="SessionLimitOptions.IdleLockMinutes"/> and flips them to locked. The
/// manual surface here lets the user explicitly lock their session ("step away from
/// the desk") without waiting for the idle timer.
/// </para>
/// <para>
/// <b>Audit.</b> Both lock paths write an <see cref="AuditSeverity.Notice"/> audit
/// row — manual locks use event code <c>USER.SESSION.LOCKED_MANUAL</c> and auto
/// locks use <c>USER.SESSION.LOCKED_AUTO</c>. Unlocks write
/// <c>USER.SESSION.UNLOCKED_MANUAL</c>. PII is never carried in the audit payload
/// per SEC 044.
/// </para>
/// </remarks>
public interface ISessionLockService
{
    /// <summary>
    /// Locks the caller's current session. Resolves the row via
    /// <c>ICallerContext.SessionId</c> and sets <see cref="UserSession.IsLocked"/> +
    /// <see cref="UserSession.LockedAtUtc"/>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// 200 with the updated <see cref="UserSessionDto"/>; 401 when the caller has
    /// no session id; 404 when the session row cannot be located (or has already
    /// been terminated).
    /// </returns>
    Task<Result<UserSessionDto>> LockCurrentSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// Unlocks the caller's current session — symmetric to
    /// <see cref="LockCurrentSessionAsync"/>. Clears
    /// <see cref="UserSession.IsLocked"/> and <see cref="UserSession.LockedAtUtc"/>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// 200 with the updated <see cref="UserSessionDto"/>; 401 / 404 mirror the
    /// lock semantics.
    /// </returns>
    Task<Result<UserSessionDto>> UnlockCurrentSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// Cheap middleware-side probe — returns <c>true</c> when the supplied session
    /// id maps to a row with <see cref="UserSession.IsLocked"/> = <c>true</c>.
    /// Locked-and-terminated rows count as locked (defensive — a terminated
    /// session must never be honoured even if the lock flag is unset). Unknown
    /// session ids return <c>false</c> so genuinely-anonymous probes don't
    /// false-positive.
    /// </summary>
    /// <param name="sessionId">Opaque session identifier (JWT jti / family id).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when the session is locked or terminated; <c>false</c> otherwise.</returns>
    Task<bool> IsLockedAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Lists the caller's currently-active sessions (IsTerminated=false), newest
    /// first. The "show me where I'm signed in" surface for the citizen / staff
    /// profile.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with the list; 401 when the caller is anonymous.</returns>
    Task<Result<IReadOnlyList<UserSessionDto>>> ListMineAsync(CancellationToken ct = default);

    /// <summary>
    /// Admin-only force-terminate of a specific session belonging to a specific
    /// user. Sets <see cref="UserSession.IsTerminated"/>=<c>true</c> +
    /// <see cref="UserSession.TerminatedAtUtc"/> + a stable
    /// <c>TerminationReason="AdminForceTerminate"</c>. Writes an audit Critical
    /// row.
    /// </summary>
    /// <param name="userSqid">Sqid-encoded id of the session owner.</param>
    /// <param name="sessionSqid">Sqid-encoded id of the session row to kill.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// 204-equivalent <see cref="Result.Success()"/>; 403 (caller lacks admin);
    /// 404 (session/user not found); 400 (invalid sqid).
    /// </returns>
    Task<Result> AdminTerminateAsync(string userSqid, string sessionSqid, CancellationToken ct = default);
}
