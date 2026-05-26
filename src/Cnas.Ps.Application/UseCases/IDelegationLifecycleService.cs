using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R0057 / TOR SEC 026 + CF 16.11 — lifecycle service backing the
/// <c>POST/GET/DELETE /api/delegations</c> surface for time-bounded permission grants.
/// Encapsulates the grant / revoke / list flow over the <c>DelegationGrant</c> entity
/// and emits Critical-severity audit rows for every mutation so an investigator can
/// later reconstruct who acted on behalf of whom.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sqid contract.</b> All identifiers crossing the API boundary are Sqid-encoded
/// per CLAUDE.md RULE 3. Returned DTOs carry Sqid strings; inputs accept Sqid strings
/// and decode them internally.
/// </para>
/// <para>
/// <b>Authentication required.</b> Every entry point assumes an authenticated caller.
/// The controller surface is <c>[Authorize]</c>-gated; the service performs a
/// defense-in-depth check via <c>ICallerContext.UserId</c> and returns
/// <see cref="ErrorCodes.Unauthorized"/> when absent.
/// </para>
/// </remarks>
public interface IDelegationLifecycleService
{
    /// <summary>
    /// Issues a new delegation grant from the calling user (grantor) to the supplied
    /// delegatee. Validations enforced: window is forward-only and ≤ 90 days, scope is
    /// non-empty, delegatee ≠ grantor, both user rows exist + active.
    /// </summary>
    /// <param name="delegateeSqid">Sqid-encoded id of the delegatee user.</param>
    /// <param name="validFromUtc">Inclusive UTC start of the delegation window.</param>
    /// <param name="validToUtc">Exclusive UTC end of the delegation window.</param>
    /// <param name="suspendsGrantorRights">
    /// When <c>true</c>, the grantor's own rights covered by <paramref name="scope"/>
    /// are suspended for the window — only the delegatee may exercise them.
    /// </param>
    /// <param name="scope">Free-text scope discriminator (1..128 chars).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly-persisted grant projected as a <see cref="DelegationGrantDto"/>.</returns>
    Task<Result<DelegationGrantDto>> GrantAsync(
        string delegateeSqid,
        DateTime validFromUtc,
        DateTime validToUtc,
        bool suspendsGrantorRights,
        string scope,
        CancellationToken ct = default);

    /// <summary>
    /// Revokes an active grant ahead of its natural expiry. The row is NEVER
    /// hard-deleted — revocation stamps <c>RevokedAtUtc</c> + <c>RevokeReason</c> so
    /// the audit trail (SEC 042) can reconstruct the timeline. Only the original
    /// grantor or an administrator may revoke (admin gate is delegated to the
    /// controller's <c>[Authorize]</c> policy; the service enforces the grantor check).
    /// </summary>
    /// <param name="grantSqid">Sqid-encoded id of the grant to revoke.</param>
    /// <param name="reason">Free-form revocation reason; 3..500 chars per validator.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on success; otherwise a failure carrying one of
    /// <see cref="ErrorCodes.InvalidSqid"/>, <see cref="ErrorCodes.NotFound"/>,
    /// <see cref="ErrorCodes.Forbidden"/>, <see cref="ErrorCodes.Unauthorized"/>, or
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result> RevokeAsync(string grantSqid, string reason, CancellationToken ct = default);

    /// <summary>
    /// Lists the active grants issued by the supplied user. A grant is considered
    /// active at the clock's <c>UtcNow</c> when its window covers the instant and the
    /// row has not been revoked.
    /// </summary>
    /// <param name="userSqid">Sqid-encoded id of the grantor user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The active grants ordered by <c>ValidFromUtc</c> ascending.</returns>
    Task<Result<IReadOnlyList<DelegationGrantDto>>> ListActiveAsync(
        string userSqid,
        CancellationToken ct = default);
}
