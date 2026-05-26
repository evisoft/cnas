using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Web.Backend;

/// <summary>
/// Snapshot of the currently authenticated citizen, propagated to pages via
/// <see cref="Microsoft.AspNetCore.Components.CascadingValue{TValue}"/>. Pages should
/// branch on <see cref="IsAuthenticated"/> before showing user-specific content; if
/// false they should redirect the caller to the MPass login endpoint.
/// </summary>
/// <param name="IsAuthenticated">True when the cookie session resolved to a valid profile.</param>
/// <param name="Profile">The cached profile (only meaningful when <see cref="IsAuthenticated"/> is true).</param>
/// <param name="Roles">
/// Lower-cased role strings granted to the caller by the auth cookie. Used by the
/// layout to gate staff-only navigation. Defaults to an empty list — until the
/// API exposes roles on <c>/api/profile/me</c> the staff menu only appears for
/// callers whose session was populated externally (e.g. in tests). See the
/// "Pending: backend endpoint to surface roles on /api/profile/me" gap noted in
/// the staff portal work item.
/// </param>
public sealed record UserSession(
    bool IsAuthenticated,
    ProfileOutput? Profile,
    IReadOnlyList<string> Roles)
{
    /// <summary>
    /// Convenience two-arg constructor that defaults <see cref="Roles"/> to an empty list.
    /// Kept for backwards compatibility with call-sites authored before the role plumbing
    /// landed.
    /// </summary>
    /// <param name="isAuthenticated">True when the cookie session resolved to a valid profile.</param>
    /// <param name="profile">The cached profile (only meaningful when <paramref name="isAuthenticated"/> is true).</param>
    public UserSession(bool isAuthenticated, ProfileOutput? profile)
        : this(isAuthenticated, profile, Array.Empty<string>())
    {
    }

    /// <summary>
    /// Convenience anonymous session — useful as a default cascading value before the
    /// first profile probe has completed.
    /// </summary>
    public static UserSession Anonymous { get; } = new(false, null, Array.Empty<string>());

    /// <summary>The friendly display name to render in the navbar, or empty string when anonymous.</summary>
    public string DisplayName => Profile?.DisplayName ?? string.Empty;

    /// <summary>True when the caller holds any CNAS staff role (examiner / decider / admin).</summary>
    public bool IsStaff =>
        Roles.Contains("cnas-user", StringComparer.OrdinalIgnoreCase)
        || Roles.Contains("cnas-examiner", StringComparer.OrdinalIgnoreCase)
        || Roles.Contains("cnas-decider", StringComparer.OrdinalIgnoreCase)
        || Roles.Contains("cnas-admin", StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the caller can approve dossiers (șef-direcție or admin).</summary>
    public bool IsDecider =>
        Roles.Contains("cnas-decider", StringComparer.OrdinalIgnoreCase)
        || Roles.Contains("cnas-admin", StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the caller can administer users + service passports.</summary>
    public bool IsAdmin =>
        Roles.Contains("cnas-admin", StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Cookie-session probe used by the layout to discover whether the caller is signed in.
/// One probe per page-load is cached for a short window so that repeated CascadingValue
/// reads don't fan-out to the API; see CLAUDE.md §1 ("Day-1 Foundation") for why we keep
/// the auth-state check cheap.
/// </summary>
public sealed class CookieAuthState(CnasApiClient api)
{
    /// <summary>
    /// How long a successful profile-probe stays cached. Short enough that a fresh
    /// login is reflected on the next page navigation; long enough to avoid hammering
    /// the API for cascading-value consumers.
    /// </summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly CnasApiClient _api = api;
    private UserSession _cached = UserSession.Anonymous;
    private DateTimeOffset _cachedAtUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// Returns the current <see cref="UserSession"/>, probing the API at most once per
    /// <see cref="CacheTtl"/>. A failure to reach <c>/api/profile/me</c> is treated as
    /// anonymous (the caller can offer the login button).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The freshest known <see cref="UserSession"/>.</returns>
    public async Task<UserSession> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        if (DateTimeOffset.UtcNow - _cachedAtUtc < CacheTtl)
        {
            return _cached;
        }

        var probe = await _api.GetMyProfileAsync(cancellationToken).ConfigureAwait(false);
        _cached = probe.IsSuccess
            ? new UserSession(true, probe.Value)
            : UserSession.Anonymous;
        _cachedAtUtc = DateTimeOffset.UtcNow;
        return _cached;
    }

    /// <summary>
    /// Invalidates the cache (e.g. after explicit logout) so the next call refetches.
    /// </summary>
    public void Invalidate()
    {
        _cached = UserSession.Anonymous;
        _cachedAtUtc = DateTimeOffset.MinValue;
    }
}
