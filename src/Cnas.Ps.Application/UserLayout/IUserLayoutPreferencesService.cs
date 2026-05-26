using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UserLayout;

/// <summary>
/// R0535 / CF 04.07-08 — read/write surface over <c>UserProfile.LayoutPreferences</c>.
/// Companion to <c>IProfileService</c>'s notification-preferences endpoints (R0171);
/// kept as a dedicated service because the layout JSON has its own schema, its own
/// validator, and its own fail-open parse semantics.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read side.</b> <see cref="GetForCurrentUserAsync(CancellationToken)"/> returns
/// the persisted preferences for the authenticated caller. A NULL or malformed JSON
/// column returns <see cref="Cnas.Ps.Core.ValueObjects.UserLayoutPreferences.Default"/>
/// (system defaults); malformed input additionally increments
/// <c>cnas.user_layout.parse_failure</c> so operators can chart silent drift.
/// </para>
/// <para>
/// <b>Write side.</b> <see cref="SaveAsync(UserLayoutPreferencesSaveDto, CancellationToken)"/>
/// replaces the stored preferences in full (PUT semantics). The service writes one
/// Information audit row with event code <c>USER.LAYOUT.UPDATED</c> per successful
/// save — operators can chart layout-customisation adoption.
/// </para>
/// </remarks>
public interface IUserLayoutPreferencesService
{
    /// <summary>
    /// Returns the calling user's layout preferences. When the persisted JSON is
    /// NULL or malformed, returns the
    /// <see cref="Cnas.Ps.Core.ValueObjects.UserLayoutPreferences.Default"/> shape
    /// (every grid uses its registry defaults; the system page size is the dispatcher
    /// default).
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// The preferences DTO. Returns the defaults rather than failing so the dashboard
    /// can render even when the caller is anonymous OR the user row is gone — the
    /// fail-open contract is documented on the value object's remarks.
    /// </returns>
    Task<UserLayoutPreferencesDto> GetForCurrentUserAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the calling user's layout preferences in full (PUT semantics). The
    /// service validates the input, persists the JSON, writes an Information audit
    /// row with event code <c>USER.LAYOUT.UPDATED</c>, and echoes the persisted DTO
    /// back to the caller for confirmation.
    /// </summary>
    /// <param name="input">
    /// The new preferences to persist. Must not be <c>null</c>. The validator
    /// enforces: DefaultPageSize ∈ [10, 200]; per-grid PageSize ∈ [10, 200]; grid
    /// keys match the kebab-case pattern.
    /// </param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the persisted preferences;
    /// <see cref="ErrorCodes.Unauthorized"/> when the caller is anonymous;
    /// <see cref="ErrorCodes.NotFound"/> when the underlying user row is gone;
    /// <see cref="ErrorCodes.ValidationFailed"/> when the input fails validation.
    /// </returns>
    Task<Result<UserLayoutPreferencesDto>> SaveAsync(
        UserLayoutPreferencesSaveDto input,
        CancellationToken cancellationToken = default);
}
