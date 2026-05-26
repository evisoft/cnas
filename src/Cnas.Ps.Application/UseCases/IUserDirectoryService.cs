using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// Synchronises an authenticated principal (typically returned by an MPass OIDC sign-in)
/// into the local <c>cnas.UserProfiles</c> table. Owned by the authentication pipeline;
/// the call MUST be safe to invoke on every sign-in (idempotent upsert).
/// </summary>
/// <remarks>
/// This is an internal service — its <see cref="UpsertOnSignInAsync"/> method returns the
/// raw <see cref="long"/> primary key rather than a Sqid because the sole caller is the
/// authentication pipeline running on the server side. Sqid encoding happens only at
/// outbound API boundaries (CLAUDE.md RULE 3).
/// </remarks>
public interface IUserDirectoryService
{
    /// <summary>
    /// Inserts a new <c>UserProfile</c> row when no profile matches
    /// <paramref name="externalSub"/>, or updates display name, email and role assignments
    /// on an existing row. Always emits a <c>USER_DIRECTORY.SIGN_IN_SYNC</c> audit event.
    /// </summary>
    /// <param name="externalSub">
    /// MPass subject claim (<c>sub</c>) — the external stable identity of the caller.
    /// Must be non-empty; the failure result uses <see cref="ErrorCodes.ValidationFailed"/>.
    /// </param>
    /// <param name="displayName">
    /// Human-readable name to show in the UI. Sourced from the <c>name</c> claim, falling
    /// back to the OIDC <c>preferred_username</c>. Must be non-empty (the caller is expected
    /// to substitute a placeholder when the IdP omits it).
    /// </param>
    /// <param name="email">Optional email address (<c>email</c> claim). Persisted as-is.</param>
    /// <param name="roles">
    /// Mapped CNAS role codes derived from the MPass role claims. Empty means "no roles
    /// — citizen with read-only access"; the upsert still creates the profile so the next
    /// sign-in can pick up new roles without manual intervention.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success, the internal <see cref="long"/> primary key of the user profile. On
    /// failure: <see cref="ErrorCodes.ValidationFailed"/> for empty input, or
    /// <see cref="ErrorCodes.Forbidden"/> when the profile is locked (sign-in must abort).
    /// </returns>
    Task<Result<long>> UpsertOnSignInAsync(
        string externalSub,
        string displayName,
        string? email,
        IReadOnlyCollection<string> roles,
        CancellationToken ct = default);
}
