using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>UC13 — Manage applicant profile. Self-service profile updates.</summary>
public interface IProfileService
{
    /// <summary>Returns the calling user's profile.</summary>
    Task<Result<ProfileOutput>> GetMineAsync(CancellationToken cancellationToken = default);

    /// <summary>Updates contact + language preferences for the calling user.</summary>
    Task<Result> UpdateMineAsync(ProfileUpdateInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// R0361 / UC13 — updates the caller's self-service contact fields
    /// (display name, e-mail, phone). Distinct from
    /// <see cref="UpdateMineAsync(ProfileUpdateInput, CancellationToken)"/>
    /// because it does NOT touch <c>PreferredLanguage</c>: the language
    /// toggle has its own thin endpoint at <c>PUT /api/profile/language</c>
    /// (R0211) and the contact form on <c>MyProfile.razor</c> would otherwise
    /// silently overwrite a freshly-switched language with the stale value
    /// from the form bag.
    /// </summary>
    /// <param name="input">Allowed mutations — DisplayName (required), Email, Phone.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> on success;
    /// <see cref="ErrorCodes.Unauthorized"/> when the caller is anonymous;
    /// <see cref="ErrorCodes.NotFound"/> when the underlying user row has
    /// been deactivated;
    /// <see cref="ErrorCodes.InvalidPhone"/> when <c>Phone</c> fails E.164
    /// validation;
    /// <see cref="ErrorCodes.ValidationFailed"/> when <c>DisplayName</c> is
    /// missing or <c>Email</c> is malformed.
    /// </returns>
    Task<Result> UpdateMyContactAsync(ProfileContactInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the calling user's notification-channel preferences (R0171, CF 22.02 / CF 04.08).
    /// When the persisted JSON is NULL or malformed, returns the dispatcher's
    /// <c>NotificationPreferences.Default</c> (every channel opted IN) — see the value object
    /// remarks for the fail-open contract.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the preferences DTO;
    /// <see cref="ErrorCodes.Unauthorized"/> when the caller is anonymous;
    /// <see cref="ErrorCodes.NotFound"/> when the underlying user row is gone.
    /// </returns>
    Task<Result<NotificationPreferencesDto>> GetNotificationPreferencesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the calling user's notification-channel preferences (PUT semantics, NOT PATCH).
    /// Whole-object replacement keeps the wire shape simple; the caller is expected to send
    /// the full preferences object every time.
    /// </summary>
    /// <param name="preferences">The new preferences to persist. Must not be <c>null</c>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> on success;
    /// <see cref="ErrorCodes.Unauthorized"/> when the caller is anonymous;
    /// <see cref="ErrorCodes.NotFound"/> when the underlying user row is gone;
    /// <see cref="ErrorCodes.ValidationFailed"/> when any category key exceeds 64 chars
    /// (defense against pathological JSON growth on the row).
    /// </returns>
    Task<Result> SetNotificationPreferencesAsync(
        NotificationPreferencesDto preferences,
        CancellationToken cancellationToken = default);
}
