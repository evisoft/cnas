using System.Collections.Frozen;
using System.Security.Claims;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Infrastructure.MGov;
using Cnas.Ps.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Cnas.Ps.Api.Composition;

/// <summary>
/// Wires authentication: cookie session + MPass OIDC challenge (SEC 014).
/// Local username/password is intentionally NOT registered here — it lives in a
/// separate <c>Local</c> scheme used only by the "Utilizator autorizat" role per SEC 014.
/// </summary>
public static class AuthenticationComposition
{
    /// <summary>
    /// Static MPass-role → CNAS-role lookup. MPass advertises group memberships under the
    /// <c>mpass:cnas/&lt;short-name&gt;</c> convention; we translate those to the stable
    /// internal role codes the rest of the system reasons about (see SEC 021-026).
    /// </summary>
    /// <remarks>
    /// Use <see cref="FrozenDictionary{TKey, TValue}"/> for hot-path read performance —
    /// every successful sign-in hits this table.
    /// </remarks>
    private static readonly FrozenDictionary<string, string> MPassRoleMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mpass:cnas/citizen"] = "cnas-user",
            ["mpass:cnas/examiner"] = "cnas-user",
            ["mpass:cnas/decider"] = "cnas-decider",
            ["mpass:cnas/admin"] = "cnas-admin",
            ["mpass:cnas/tech-admin"] = "cnas-tech-admin",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Claim types that MPass may use to advertise roles/groups. We probe all three on
    /// every token so the integration works across MPass deployments that differ in
    /// claim naming convention.
    /// </summary>
    private static readonly string[] RoleClaimTypes = ["role", "roles", "groups"];

    /// <summary>Adds the cookie + MPass OIDC schemes.</summary>
    public static IServiceCollection AddCnasAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var mgov = configuration.GetSection(MGovOptions.SectionName).Get<MGovOptions>() ?? new MGovOptions();
        var environmentName = configuration["ASPNETCORE_ENVIRONMENT"]
                              ?? configuration["DOTNET_ENVIRONMENT"]
                              ?? Environments.Production;
        var allowInsecureCookie = string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(environmentName, "Test", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);

        var authBuilder = services
            .AddAuthentication(opts =>
            {
                opts.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                opts.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, opts =>
            {
                opts.Cookie.Name = "cnas.session";
                opts.Cookie.HttpOnly = true;
                opts.Cookie.SecurePolicy = allowInsecureCookie
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
                opts.Cookie.SameSite = SameSiteMode.Lax;
                opts.ExpireTimeSpan = TimeSpan.FromMinutes(15);    // SEC 017 — 15 min default
                opts.SlidingExpiration = true;
                opts.LoginPath = "/api/auth/login";
                opts.LogoutPath = "/api/auth/logout";
            });

        // R0053 / SEC 018 — JWT bearer scheme alongside the cookie + OIDC chain. Used by
        // API clients (mobile / SPA / external integrations) that do not carry the cookie
        // session. The cookie scheme stays the DEFAULT so browser flows are unaffected;
        // [Authorize] policies that only require an authenticated principal accept either
        // scheme transparently. The signing key is validated for length at startup by
        // <see cref="InfrastructureServiceCollectionExtensions.AddCnasInfrastructure"/> —
        // here we read the configured value once into the validation parameters.
        var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>();
        if (jwt is not null
            && !string.IsNullOrWhiteSpace(jwt.SigningKey)
            && !string.IsNullOrWhiteSpace(jwt.Issuer)
            && !string.IsNullOrWhiteSpace(jwt.Audience))
        {
            authBuilder.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, opts =>
            {
                opts.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(jwt.SigningKey)),
                };
            });
        }

        if (!string.IsNullOrWhiteSpace(mgov.MPassIssuer) && !string.IsNullOrWhiteSpace(mgov.MPassClientId))
        {
            authBuilder.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, opts =>
            {
                opts.Authority = mgov.MPassIssuer;
                opts.ClientId = mgov.MPassClientId;
                opts.ClientSecret = mgov.MPassClientSecret;
                opts.ResponseType = OpenIdConnectResponseType.Code;
                opts.UsePkce = true;
                opts.SaveTokens = false;
                opts.Scope.Clear();
                opts.Scope.Add("openid");
                opts.Scope.Add("profile");
                opts.GetClaimsFromUserInfoEndpoint = true;
                opts.TokenValidationParameters.NameClaimType = ClaimTypes.Name;
                opts.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;

                // OnTokenValidated runs after the OIDC handler has built the ClaimsPrincipal
                // but before the cookie is issued. We use the hook to (a) translate MPass
                // role claims into CNAS role claims that the authorization policies expect
                // and (b) mirror the user into the local UserProfile table.
                opts.Events.OnTokenValidated = OnTokenValidatedAsync;
            });
        }

        return services;
    }

    /// <summary>
    /// OIDC <c>OnTokenValidated</c> handler. Performs MPass-role → CNAS-role mapping and
    /// triggers the local profile upsert via <see cref="IUserDirectoryService"/>.
    /// </summary>
    /// <param name="context">
    /// The validated-token event payload provided by the OIDC handler; mutated in place
    /// by appending mapped role claims to the existing <see cref="ClaimsIdentity"/>.
    /// </param>
    /// <remarks>
    /// Profile-sync failures (DB unavailable, transient EF Core fault) MUST NOT abort the
    /// sign-in — they are logged and skipped. The next sign-in retries the upsert.
    /// </remarks>
    private static async Task OnTokenValidatedAsync(
        Microsoft.AspNetCore.Authentication.OpenIdConnect.TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (principal?.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        // 1. Extract MPass role/group claims from any of the supported claim types.
        //    Use a HashSet so the same role advertised twice (e.g. once as "role" and
        //    once as "groups") collapses to a single mapped CNAS role.
        var mpassRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claimType in RoleClaimTypes)
        {
            foreach (var c in identity.FindAll(claimType))
            {
                mpassRoles.Add(c.Value);
            }
        }

        // 2. Translate each MPass role to its CNAS counterpart and append it to the
        //    identity as ClaimTypes.Role so the authorization handler sees it.
        var cnasRoles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mpassRole in mpassRoles)
        {
            if (MPassRoleMap.TryGetValue(mpassRole, out var cnasRole))
            {
                cnasRoles.Add(cnasRole);
            }
        }
        foreach (var cnasRole in cnasRoles)
        {
            // Avoid duplicate claims if the IdP already emits the CNAS role directly.
            if (!identity.HasClaim(ClaimTypes.Role, cnasRole))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, cnasRole));
            }
        }

        // 3. Mirror the principal into UserProfiles. Sync failures MUST NOT block sign-in.
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(sub))
        {
            return;
        }

        var displayName = principal.FindFirstValue(ClaimTypes.Name)
                          ?? principal.FindFirstValue("name")
                          ?? principal.FindFirstValue("preferred_username")
                          ?? sub;
        var email = principal.FindFirstValue(ClaimTypes.Email)
                    ?? principal.FindFirstValue("email");

        // The OIDC pipeline resolves services through HttpContext.RequestServices because
        // the directory service is scoped (per-request EF Core context).
        var directory = context.HttpContext.RequestServices
            .GetService<IUserDirectoryService>();
        if (directory is null)
        {
            return;
        }

        try
        {
            await directory.UpsertOnSignInAsync(
                externalSub: sub,
                displayName: displayName,
                email: email,
                roles: [.. cnasRoles],
                ct: context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Sign-in is more important than sync; sync retries on the user's next visit.
            var logger = context.HttpContext.RequestServices
                .GetService<ILoggerFactory>()
                ?.CreateLogger("Cnas.Ps.Api.Auth");
            logger?.LogWarning(ex, "User directory upsert failed for sub={Sub}; sign-in proceeds.", sub);
        }
    }
}
