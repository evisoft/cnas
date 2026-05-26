using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R0059 / SEC 016 — Account state-machine service. Centralises every transition between
/// <see cref="UserAccountState"/> values and produces the mandatory critical-severity
/// audit row on success. Direct mutations of <see cref="UserProfile.State"/> from other
/// paths bypass the transition matrix and the audit obligation; always route through
/// this service.
/// </summary>
/// <remarks>
/// <para>
/// <b>Allowed transitions (deny-by-default).</b>
/// <list type="bullet">
///   <item><see cref="UserAccountState.Active"/> → <c>{ Suspended, Disabled, Locked }</c></item>
///   <item><see cref="UserAccountState.Suspended"/> → <c>{ Active, Disabled }</c></item>
///   <item><see cref="UserAccountState.Locked"/> → <c>{ Active, Disabled }</c> (admin unlock)</item>
///   <item><see cref="UserAccountState.Disabled"/> → <c>{ Active }</c> (rare elevated reactivation)</item>
/// </list>
/// Anything else returns
/// <see cref="ErrorCodes.UserAccountStateTransitionForbidden"/>.
/// </para>
/// <para>
/// <b>Audit row.</b> On every successful transition the service writes an
/// <c>AuditLog</c> entry with event code
/// <c>USER.STATE_CHANGE.&lt;FROM&gt;.&lt;TO&gt;</c> and severity
/// <see cref="AuditSeverity.Critical"/>. The target id is the user's raw <c>Id</c> on
/// the <c>TargetEntityId</c> column; the actor id is the admin's Sqid via
/// <c>ICallerContext.UserSqid</c> (or the literal <c>"system"</c> for the auto-lock path).
/// The <c>DetailsJson</c> payload carries <c>{ "from": "...", "to": "...", "reason": "..." }</c>
/// — never the user's IDNP, email, or any other PII (SEC 044).
/// </para>
/// </remarks>
public interface IUserAccountStateService
{
    /// <summary>
    /// Transitions the account to <paramref name="newState"/> after validating the
    /// transition is permitted by the state machine. Writes the mandatory audit row
    /// and saves changes in a single unit-of-work.
    /// </summary>
    /// <param name="userSqid">Sqid-encoded user id of the target account.</param>
    /// <param name="newState">Desired new state.</param>
    /// <param name="reason">Optional free-form reason captured on the audit row; may be null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> on success;
    /// <see cref="ErrorCodes.Forbidden"/> when the caller lacks the admin role;
    /// <see cref="ErrorCodes.InvalidSqid"/> when the sqid cannot be decoded;
    /// <see cref="ErrorCodes.NotFound"/> when the user does not exist or is soft-deleted;
    /// <see cref="ErrorCodes.UserAccountStateTransitionForbidden"/> when the requested
    /// transition is not in the allow-list.
    /// </returns>
    Task<Result> ChangeStateAsync(string userSqid, UserAccountState newState, string? reason, CancellationToken ct = default);

    /// <summary>
    /// Auto-lock convenience used by the failed-login pipeline. Idempotent — calling on
    /// a user already in <see cref="UserAccountState.Locked"/> returns success without
    /// re-writing the audit row. Returns
    /// <see cref="ErrorCodes.UserAccountStateTransitionForbidden"/> when the user is in
    /// <see cref="UserAccountState.Disabled"/> (a disabled account does not need locking;
    /// the disabled state already rejects sign-in).
    /// </summary>
    /// <param name="userId">Internal user primary key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result code per the remarks.</returns>
    Task<Result> LockForFailedLoginsAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// R2263 / SEC 016 — bulk Active → Suspended transition. Iterates the supplied
    /// user list (de-duplicated by the service), flips every <c>Active</c> account to
    /// <c>Suspended</c>, and writes one <see cref="AuditSeverity.Critical"/> audit row
    /// per successful transition. Skipped rows (already suspended, not found, sqid
    /// decode failure) are reported on the result's <c>Failures</c> list with the
    /// stable error code; the operation never throws and never partial-fails the
    /// entire run because of a single bad row.
    /// </summary>
    /// <param name="userSqids">Sqid-encoded ids of the target users (1..200 per validator).</param>
    /// <param name="reason">Free-text reason captured on every per-user audit row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="UserAccountStateBulkResultDto"/> describing successes + per-row
    /// failures. Returns <c>Result.Failure</c> only on caller-wide rejections (e.g.
    /// missing admin role).
    /// </returns>
    Task<Result<UserAccountStateBulkResultDto>> BulkSuspendAsync(
        IReadOnlyList<string> userSqids,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// R2263 / SEC 016 — bulk Locked → Active transition. Symmetric to
    /// <see cref="BulkSuspendAsync"/>; flips every <c>Locked</c> row to
    /// <c>Active</c> and writes one <see cref="AuditSeverity.Critical"/> audit row
    /// per success. Skipped rows are reported on the result's <c>Failures</c> list.
    /// </summary>
    /// <param name="userSqids">Sqid-encoded ids of the target users (1..200 per validator).</param>
    /// <param name="reason">Free-text reason captured on every per-user audit row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="UserAccountStateBulkResultDto"/> describing successes + per-row
    /// failures. Returns <c>Result.Failure</c> only on caller-wide rejections.
    /// </returns>
    Task<Result<UserAccountStateBulkResultDto>> BulkUnlockAsync(
        IReadOnlyList<string> userSqids,
        string reason,
        CancellationToken ct = default);
}
