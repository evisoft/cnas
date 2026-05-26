using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// Mints, rotates, and revokes opaque refresh tokens for the R0053 token pipeline
/// (CLAUDE.md §5.3 / SEC 018). Paired with <see cref="IJwtTokenIssuer"/>, which
/// produces the short-lived JWT access token the caller exchanges every ~15 minutes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hash, not plaintext.</b> The plaintext refresh token is returned to the caller
/// exactly once (in <see cref="RefreshTokenIssueResult.OpaqueToken"/>) and NEVER
/// persisted; the store keeps only the SHA-256 hash. A database compromise therefore
/// does not yield usable refresh tokens.
/// </para>
/// <para>
/// <b>Rotation.</b> <see cref="RotateAsync"/> consumes the presented token (sets
/// <c>ConsumedAtUtc</c>) and inserts a fresh child row sharing the same
/// <see cref="RefreshTokenIssueResult.FamilyId"/>. The caller MUST replace its stored
/// refresh token with the new value on every successful rotation; failure to do so
/// will trigger reuse-detection on the next call.
/// </para>
/// <para>
/// <b>Reuse-detection.</b> Re-presenting an already-consumed refresh token is treated
/// as a stolen-credential signal — the service revokes EVERY live token in the same
/// family before returning <see cref="ErrorCodes.RefreshTokenReused"/>. Both the
/// legitimate user and the attacker lose access simultaneously; the user must
/// re-authenticate via the primary login flow.
/// </para>
/// <para>
/// <b>Logout.</b> <see cref="RevokeFamilyAsync"/> is idempotent — calling it with an
/// unknown token returns <see cref="Result.Success"/> rather than erroring out.
/// </para>
/// </remarks>
public interface IRefreshTokenService
{
    /// <summary>
    /// Mints a NEW refresh-token family for a fresh login. Inserts a single row whose
    /// <see cref="RefreshTokenIssueResult.FamilyId"/> is a freshly-generated GUID and
    /// whose parent id is <c>null</c>. The returned plaintext is the only copy — the
    /// caller MUST store it (in a secure HTTP-only cookie or equivalent) and present
    /// it back to <see cref="RotateAsync"/> when exchanging for a new access token.
    /// </summary>
    /// <param name="userId">Internal user primary key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success"/> with the opaque token + family id + expiry on
    /// success; reserved for future failure codes (no failure path today since the
    /// caller has already authenticated by the time IssueAsync runs).
    /// </returns>
    Task<Result<RefreshTokenIssueResult>> IssueAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// Rotates the presented refresh token. On success consumes the current row and
    /// returns a fresh child token sharing the same family. On reuse-detected revokes
    /// the entire family before returning <see cref="ErrorCodes.RefreshTokenReused"/>.
    /// On a non-Active underlying user revokes the family and returns
    /// <see cref="ErrorCodes.RefreshTokenRevoked"/>.
    /// </summary>
    /// <param name="opaqueRefreshToken">Plaintext refresh token presented by the caller.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success"/> with the new token / family / expiry on
    /// successful rotation; one of <see cref="ErrorCodes.RefreshTokenInvalid"/>,
    /// <see cref="ErrorCodes.RefreshTokenExpired"/>,
    /// <see cref="ErrorCodes.RefreshTokenRevoked"/>, or
    /// <see cref="ErrorCodes.RefreshTokenReused"/> otherwise.
    /// </returns>
    Task<Result<RefreshTokenIssueResult>> RotateAsync(string opaqueRefreshToken, CancellationToken ct = default);

    /// <summary>
    /// Revokes EVERY live token in the family identified by the supplied refresh
    /// token. Used by the logout endpoint. Idempotent — calling with an unknown
    /// token returns <see cref="Result.Success"/> rather than erroring out, so the
    /// client cannot infer token existence by probing logout responses.
    /// </summary>
    /// <param name="opaqueRefreshToken">Plaintext refresh token presented by the caller.</param>
    /// <param name="reason">Short stable reason captured on every revoked row (e.g. <c>"logout"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Always <see cref="Result.Success"/> — see remarks on idempotency.</returns>
    Task<Result> RevokeFamilyAsync(string opaqueRefreshToken, string reason, CancellationToken ct = default);
}

/// <summary>
/// Payload returned by <see cref="IRefreshTokenService.IssueAsync"/> /
/// <see cref="IRefreshTokenService.RotateAsync"/>. Carries the plaintext refresh
/// token (the only copy the caller will ever see), the family id, and the UTC
/// expiry instant.
/// </summary>
/// <param name="OpaqueToken">
/// Base64url-encoded random refresh token. The caller MUST store this securely and
/// present it back to <c>RotateAsync</c> when exchanging for a new access token. The
/// plaintext is NEVER persisted server-side; only its SHA-256 hash lives in the
/// database.
/// </param>
/// <param name="FamilyId">
/// GUID identifying the rotation chain. All rows produced by rotating from a single
/// login event share the same family id; a logout or reuse-detected event revokes
/// every row in the family together.
/// </param>
/// <param name="ExpiresAtUtc">
/// UTC instant after which the refresh token will be rejected by
/// <c>RotateAsync</c>. Pinned to <c>JwtOptions.RefreshTokenLifetime</c> at issue
/// time (30 days per SEC 018 default).
/// </param>
/// <param name="UserId">
/// Internal user id this token authenticates. Surfaced so the AuthController can mint
/// a JWT access token against the same identity without re-querying the database; the
/// refresh service has already resolved the user (and applied the account-state gate)
/// before returning.
/// </param>
public sealed record RefreshTokenIssueResult(string OpaqueToken, Guid FamilyId, DateTime ExpiresAtUtc, long UserId);
