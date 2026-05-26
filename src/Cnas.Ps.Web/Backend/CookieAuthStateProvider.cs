using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Cnas.Ps.Web.Backend;

/// <summary>
/// Blazor <see cref="AuthenticationStateProvider"/> bridge that surfaces the
/// CNAS cookie session to the framework's authorization plumbing. The provider
/// delegates the actual probe to <see cref="CookieAuthState"/> (which caches a
/// successful profile fetch for <see cref="CookieAuthState.CacheTtl"/>) and
/// projects the returned <see cref="UserSession"/> into a
/// <see cref="ClaimsPrincipal"/> the framework's <c>AuthorizeView</c> +
/// <c>AuthorizeRouteView</c> can branch on.
/// </summary>
/// <remarks>
/// Each role string in <see cref="UserSession.Roles"/> is emitted as a
/// <see cref="ClaimTypes.Role"/> claim so policy gates such as
/// <c>RequireRole("cnas-admin")</c> resolve correctly. Anonymous sessions
/// return an unauthenticated <see cref="ClaimsPrincipal"/> with an empty
/// identity — never <c>null</c> — to match the framework's contract.
/// </remarks>
public sealed class CookieAuthStateProvider : AuthenticationStateProvider
{
    /// <summary>Authentication scheme name carried by the synthesized identity.</summary>
    /// <remarks>
    /// Picked to match the cookie scheme name used by the backend API. The exact value
    /// is only meaningful as a signal that the identity is authenticated; framework
    /// code branches on <see cref="ClaimsIdentity.IsAuthenticated"/> which requires a
    /// non-empty authentication type.
    /// </remarks>
    private const string AuthenticationType = "CnasCookie";

    /// <summary>Backing cookie-session probe shared with the layout cascading value.</summary>
    private readonly CookieAuthState _state;

    /// <summary>
    /// Initializes a new <see cref="CookieAuthStateProvider"/>.
    /// </summary>
    /// <param name="state">Shared cookie-session probe; must not be null.</param>
    public CookieAuthStateProvider(CookieAuthState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var session = await _state.GetSessionAsync().ConfigureAwait(false);
        return new AuthenticationState(BuildPrincipal(session));
    }

    /// <summary>
    /// Tells the framework that the cached identity is stale (e.g. after an
    /// explicit logout) so the next <see cref="AuthorizeView"/> render refetches
    /// the session.
    /// </summary>
    public void NotifyAuthenticationStateChanged()
    {
        // Force the cookie cache to expire so the next GetAuthenticationStateAsync
        // call hits the API rather than serving a stale snapshot.
        _state.Invalidate();
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    /// <summary>
    /// Projects the supplied <see cref="UserSession"/> into a
    /// <see cref="ClaimsPrincipal"/>: anonymous sessions yield an empty
    /// principal, authenticated sessions yield a principal carrying a
    /// <see cref="ClaimTypes.Name"/> + a <see cref="ClaimTypes.Role"/> per role.
    /// </summary>
    /// <param name="session">The session snapshot to project.</param>
    /// <returns>The synthesized principal.</returns>
    private static ClaimsPrincipal BuildPrincipal(UserSession session)
    {
        if (!session.IsAuthenticated)
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        var claims = new List<Claim>
        {
            // Display name doubles as the principal's name claim — used by
            // AuthorizeView's `<Authorized Context="ctx">` consumers.
            new(ClaimTypes.Name, session.DisplayName),
        };

        // Each role string surfaces as a separate ClaimTypes.Role claim so
        // RequireRole("cnas-admin") in the policy bag resolves correctly.
        foreach (var role in session.Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(claims, AuthenticationType);
        return new ClaimsPrincipal(identity);
    }
}
