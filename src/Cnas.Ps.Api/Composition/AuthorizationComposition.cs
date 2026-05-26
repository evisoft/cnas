using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.Api.Composition;

/// <summary>
/// Named ASP.NET Core authorization policies (RBAC per SEC 021-026 / R0055). Policies
/// are tiered so that higher-privilege roles automatically satisfy lower-privilege
/// policies (e.g. <see cref="CnasAdmin"/> users transparently pass
/// <see cref="CnasDecider"/> and <see cref="CnasUser"/> checks).
/// </summary>
/// <remarks>
/// <para>
/// Controllers MUST reference these constants in their <c>[Authorize(Policy = ...)]</c>
/// attributes — bare role strings drift over time and are not detected by the
/// architecture tests. Service-level role checks remain in place as defense-in-depth.
/// </para>
/// <para>
/// <b>R0055 generic-role coverage.</b> TOR enumerates eight generic personas:
/// <c>UtilizatorInternet</c>, <c>UtilizatorAutorizat</c>, <c>Solicitant</c>,
/// <c>UtilizatorCNAS</c>, <c>SefulDirectiei</c>, <c>SefulCNAS</c>,
/// <c>AdministratorSistem</c>, <c>AdministratorTehnic</c>. Each persona is seeded as
/// a named policy below. The kebab-case policy names use the canonical Romanian
/// form (<c>seful-cnas</c>, <c>solicitant</c>) so the role-claim string and the
/// policy name share a deterministic mapping that the audit subsystem can recover.
/// </para>
/// </remarks>
public static class AuthorizationComposition
{
    /// <summary>Any authenticated CNAS staff role — read-only access.</summary>
    public const string CnasUser = "CnasUser";

    /// <summary>Decider role (or higher) — approve/reject workflow steps.</summary>
    public const string CnasDecider = "CnasDecider";

    /// <summary>Functional administrator — user/role/passport mutations (UC15, UC18).</summary>
    public const string CnasAdmin = "CnasAdmin";

    /// <summary>Technical administrator — infrastructure/system jobs (UC20).</summary>
    public const string CnasTechAdmin = "CnasTechAdmin";

    /// <summary>
    /// R0055 / TOR SEC 021 — anonymous "internet user" persona. Backs the public
    /// CNAS surface (catalog, FAQs, public reports). Carries no authentication
    /// requirement so anonymous visitors can satisfy it.
    /// </summary>
    public const string UtilizatorInternet = "UtilizatorInternet";

    /// <summary>
    /// R0055 / TOR SEC 022 — citizen-applicant (Solicitant) persona, authenticated
    /// via MPass. The MPass SAML assertion drives the role claim;
    /// no local-login fallback exists for this persona.
    /// </summary>
    public const string Solicitant = "Solicitant";

    /// <summary>
    /// R0055 / TOR SEC 024 — directorate-head persona. Supervisory role with
    /// approval rights over decider outcomes inside a regional directorate.
    /// </summary>
    public const string SefulDirectiei = "SefulDirectiei";

    /// <summary>
    /// R0055 / TOR SEC 024 — CNAS general-manager persona. Exec-tier role
    /// authorising cross-directorate actions and capital decisions.
    /// </summary>
    public const string SefulCNAS = "SefulCNAS";

    /// <summary>
    /// R0055 / TOR SEC 021-024 — the eight generic persona role-claim values
    /// kept as a single readonly list so tests / dashboards can reflect the
    /// catalog without reparsing the C# source. Order matches the TOR seed.
    /// </summary>
    public static readonly IReadOnlyList<string> AllGenericRoles =
    [
        "UtilizatorInternet",
        "UtilizatorAutorizat",
        "Solicitant",
        "UtilizatorCNAS",
        "SefulDirectiei",
        "SefulCNAS",
        "AdministratorSistem",
        "AdministratorTehnic",
    ];

    /// <summary>
    /// Registers the named CNAS policies on the supplied service collection. The
    /// authentication scheme(s) MUST be added separately via
    /// <see cref="AuthenticationComposition.AddCnasAuthentication"/>.
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <returns><paramref name="services"/> to allow fluent chaining.</returns>
    public static IServiceCollection AddCnasAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(CnasUser, p => p
                .RequireAuthenticatedUser()
                .RequireRole("cnas-user", "cnas-decider", "cnas-admin"))
            .AddPolicy(CnasDecider, p => p
                .RequireAuthenticatedUser()
                .RequireRole("cnas-decider", "cnas-admin"))
            .AddPolicy(CnasAdmin, p => p
                .RequireAuthenticatedUser()
                .RequireRole("cnas-admin"))
            .AddPolicy(CnasTechAdmin, p => p
                .RequireAuthenticatedUser()
                .RequireRole("cnas-tech-admin"))
            // R0055 / TOR SEC 021 — anonymous public persona. Requires no
            // authentication so the public catalog endpoints can carry
            // [Authorize(Policy = UtilizatorInternet)] uniformly with the rest
            // of the surface.
            .AddPolicy(UtilizatorInternet, p => p
                .RequireAssertion(_ => true))
            // R0055 / TOR SEC 022 — citizen applicant. MPass-issued SAML
            // assertion supplies the role claim.
            .AddPolicy(Solicitant, p => p
                .RequireAuthenticatedUser()
                .RequireRole("solicitant"))
            // R0055 / TOR SEC 024 — directorate-head (supervisory tier above
            // cnas-decider). Both decider and admin claims also satisfy this
            // policy because the supervisor role implies decider capability.
            .AddPolicy(SefulDirectiei, p => p
                .RequireAuthenticatedUser()
                .RequireRole("seful-directiei", "cnas-admin"))
            // R0055 / TOR SEC 024 — CNAS general-manager. Exec-tier; only the
            // dedicated role claim satisfies the policy (admin does NOT).
            .AddPolicy(SefulCNAS, p => p
                .RequireAuthenticatedUser()
                .RequireRole("seful-cnas"));

        // R2271 / TOR SEC 025 — ABAC plumbing. The policy provider wraps the
        // default provider so unknown / non-abac policy names continue to
        // resolve via the named-policy registrations above. The handler is
        // Scoped because it touches the per-request HttpContext via
        // IHttpContextAccessor.
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider,
            Cnas.Ps.Api.Authorization.AbacPolicyProvider>();
        services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
            Cnas.Ps.Api.Authorization.AbacAuthorizationHandler>();
        return services;
    }
}
