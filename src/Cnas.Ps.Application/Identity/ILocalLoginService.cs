using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Identity;

/// <summary>
/// R0051 / TOR SEC 014 / CLAUDE.md §5.3 — local username/password sign-in for the
/// <c>UtilizatorAutorizat</c> persona only. The service composes the per-user
/// credential pipeline:
/// <list type="number">
///   <item><description>Look up the <c>UserProfile</c> by case-insensitive <c>LocalLogin</c>.</description></item>
///   <item><description>Verify the supplied password against
///     <c>UserProfile.LocalPasswordHash</c> via the Argon2id-backed
///     <c>IPasswordHasher.Verify</c>.</description></item>
///   <item><description>Gate on <c>UserAccountState == Active</c> per SEC 016.</description></item>
///   <item><description>Confirm the user's union of direct + group-inherited roles
///     contains <c>UtilizatorAutorizat</c> per SEC 014.</description></item>
///   <item><description>On success, mint a JWT access token + opaque refresh token
///     via <c>IJwtTokenIssuer</c> + <c>IRefreshTokenService</c> and register the
///     session with <c>ISessionLimitEnforcer</c> (concurrent-session cap from
///     SEC 017 applies here too).</description></item>
///   <item><description>On 5 consecutive failures, auto-lock the account via
///     <c>IUserAccountStateService.LockForFailedLoginsAsync</c>.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Account-enumeration prevention.</b> Every failure mode — unknown login, wrong
/// password, non-Active state, missing <c>UtilizatorAutorizat</c> role — returns
/// the SAME <see cref="ErrorCodes.LoginInvalid"/> code. The wire response therefore
/// reveals nothing about which condition failed. Internally each outcome is audited
/// with a distinct event sub-code (e.g. <c>USER.LOGIN.UNKNOWN</c>,
/// <c>USER.LOGIN.BAD_PASSWORD</c>, <c>USER.LOGIN.WRONG_ROLE</c>,
/// <c>USER.LOGIN.ACCOUNT_LOCKED</c>) so ops forensics retain full fidelity.
/// </para>
/// <para>
/// <b>Why a separate service.</b> The refresh-token pipeline (R0053) and the
/// session-limit / session-lock primitives (R0054) already exist. This service is
/// the orchestrator that wires the local-credential entry point into both — it
/// holds no novel state of its own. Registering it as scoped per request keeps the
/// dependency surface aligned with the underlying <c>ICnasDbContext</c>.
/// </para>
/// </remarks>
public interface ILocalLoginService
{
    /// <summary>
    /// Attempts to authenticate the supplied local credential. Returns a fully-
    /// minted token envelope on success; a uniform <see cref="ErrorCodes.LoginInvalid"/>
    /// failure on every recognised failure mode.
    /// </summary>
    /// <param name="input">
    /// Validated local-login payload (already passed through
    /// <c>LocalLoginInputValidator</c> at the controller boundary, but the service
    /// re-runs the validator defensively).
    /// </param>
    /// <param name="clientIpAddress">
    /// Source IP captured by the controller from <c>HttpContext.Connection.RemoteIpAddress</c>.
    /// Passed through to the session-limit enforcer so the issued <c>UserSession</c>
    /// row carries forensic provenance. May be <c>null</c> when the auth pipeline
    /// runs in a test fixture that does not synthesize a remote address.
    /// </param>
    /// <param name="clientUserAgent">
    /// User-Agent header captured by the controller. Same forensic-only use as
    /// <paramref name="clientIpAddress"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success"/> with the token envelope on success;
    /// <see cref="ErrorCodes.LoginInvalid"/> on every failure mode the service
    /// recognises;
    /// <see cref="ErrorCodes.ValidationFailed"/> only when the input fails the
    /// defensive validator re-check (the controller should have rejected it first).
    /// </returns>
    Task<Result<LocalLoginSuccessDto>> LoginAsync(
        LocalLoginInputDto input,
        string? clientIpAddress,
        string? clientUserAgent,
        CancellationToken ct = default);
}
