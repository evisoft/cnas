namespace Cnas.Ps.Application.Identity;

/// <summary>
/// R0051 / TOR SEC 014 / CLAUDE.md §5.3 — tracks the per-user consecutive
/// failed-login attempt counter used by <c>LocalLoginService</c> to trigger the
/// auto-lock at the configured threshold. Lives behind a small interface so the
/// default in-memory implementation can be swapped for a Redis-backed one when the
/// platform moves to a multi-instance deployment (the in-memory version is correct
/// for a single-replica process and is the explicit ship target for this iteration
/// — see R0054 follow-up "Redis-backed session store").
/// </summary>
/// <remarks>
/// <para>
/// <b>Sliding-window semantics.</b> The tracker keeps a count per user-id. Each
/// failure increments the counter; a successful login resets it to zero. Counts
/// older than the configured TTL (15 minutes by default) are forgotten so a
/// long-idle account does not lock out from a single accidental typo months ago.
/// </para>
/// <para>
/// <b>Thread-safe.</b> The default implementation is safe for concurrent access;
/// register as <c>Singleton</c> at the composition root.
/// </para>
/// <para>
/// <b>No persistence.</b> A process restart resets every counter to zero — this is
/// intentional: the actual security boundary is the eventual account lock, which
/// IS persisted on <see cref="Cnas.Ps.Core.Domain.UserProfile.State"/> via
/// <c>IUserAccountStateService.LockForFailedLoginsAsync</c>. The counter is just a
/// rate-limit signal; losing it across restarts costs the attacker at most one
/// additional bucket-full of attempts before they bump the persisted lock.
/// </para>
/// </remarks>
public interface IFailedLoginAttemptTracker
{
    /// <summary>
    /// Records a failed login attempt for <paramref name="userId"/> and returns the
    /// resulting consecutive-failure count INCLUDING this call.
    /// </summary>
    /// <param name="userId">Internal user primary key.</param>
    /// <returns>Updated failure count (≥ 1).</returns>
    int RecordFailure(long userId);

    /// <summary>
    /// Returns the current consecutive-failure count for <paramref name="userId"/>
    /// without mutating it. Used by tests; not exercised on the hot path.
    /// </summary>
    /// <param name="userId">Internal user primary key.</param>
    /// <returns>Current count (0 when none recorded or all expired).</returns>
    int GetFailureCount(long userId);

    /// <summary>
    /// Resets the counter for <paramref name="userId"/> to zero. Called by
    /// <c>LocalLoginService</c> on every successful authentication so a single
    /// typo earlier in the day does not penalise a legitimate user later.
    /// </summary>
    /// <param name="userId">Internal user primary key.</param>
    void Reset(long userId);
}
