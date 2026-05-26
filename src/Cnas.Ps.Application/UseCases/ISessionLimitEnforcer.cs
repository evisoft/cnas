using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R2264 / SEC 017 — caps the number of concurrent sessions a user may hold at any
/// instant (default 3 — see <see cref="SessionLimitOptions.MaxConcurrentSessions"/>).
/// When a fresh sign-in would push the user past the ceiling, the enforcer
/// force-terminates the OLDEST live session (FIFO eviction) and writes a
/// <c>USER.SESSION.TERMINATED_BY_LIMIT</c> critical-severity audit row so the action
/// is traceable end-to-end.
/// </summary>
/// <remarks>
/// <para>
/// <b>When to call.</b> Invoke from the auth pipeline AFTER the JWT + refresh token
/// have been minted and BEFORE the response is flushed to the client. The enforcer
/// inserts the row for the new session, counts live rows, and evicts the oldest if
/// the count exceeds the configured ceiling. The eviction is best-effort against the
/// scoped <c>ICnasDbContext</c> — if the SaveChanges call fails the new session is
/// still alive (we'd rather have one too many sessions than reject a legitimate
/// sign-in).
/// </para>
/// <para>
/// <b>Eviction policy.</b> Oldest-first per
/// <c>UserSession.CreatedAtUtc ASC</c>. The eviction sets <c>IsTerminated=true</c>,
/// <c>TerminatedAtUtc=now</c>, <c>TerminationReason="ConcurrentLimitExceeded"</c>.
/// A future enhancement may key off last-activity instead of creation time; for now
/// FIFO matches the user's intuitive "most-recently-signed-in session wins" mental
/// model.
/// </para>
/// </remarks>
public interface ISessionLimitEnforcer
{
    /// <summary>
    /// Registers a freshly-minted authenticated session and evicts the oldest live
    /// row when the concurrent-session ceiling would be exceeded.
    /// </summary>
    /// <param name="userId">Internal user primary key of the signing-in user.</param>
    /// <param name="sessionId">
    /// Opaque session identifier — typically the JWT <c>jti</c> claim or the
    /// refresh-token family id. Stored verbatim as the <c>SessionId</c> column.
    /// </param>
    /// <param name="ipAddress">Source IP captured by the auth pipeline (may be null).</param>
    /// <param name="userAgent">User-Agent string captured by the auth pipeline (may be null).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> on success (with or without a concurrent
    /// eviction); a failure result when the underlying persistence call refused
    /// the insert (concurrency conflict, duplicate session-id, etc.).
    /// </returns>
    Task<Result> RegisterNewSessionAsync(
        long userId,
        string sessionId,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default);
}
