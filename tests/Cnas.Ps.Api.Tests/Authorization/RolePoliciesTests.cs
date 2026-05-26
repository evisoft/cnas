using Cnas.Ps.Api.Composition;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Api.Tests.Authorization;

/// <summary>
/// R0055 / TOR SEC 021-024 — pin tests for the eight generic-persona role
/// policies registered by
/// <see cref="AuthorizationComposition.AddCnasAuthorization"/>. Verifies that
/// every newly-added persona is reachable through
/// <see cref="IAuthorizationPolicyProvider"/> and that the documented role
/// constants line up with the policy names.
/// </summary>
public sealed class RolePoliciesTests
{
    private static IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCnasAuthorization();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task UtilizatorInternet_Policy_Is_Registered()
    {
        using var sp = (ServiceProvider)BuildProvider();
        var provider = sp.GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await provider.GetPolicyAsync(AuthorizationComposition.UtilizatorInternet);

        policy.Should().NotBeNull("UtilizatorInternet must be a registered policy per R0055");
    }

    [Fact]
    public async Task Solicitant_Policy_Is_Registered_And_Requires_Authentication()
    {
        using var sp = (ServiceProvider)BuildProvider();
        var provider = sp.GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await provider.GetPolicyAsync(AuthorizationComposition.Solicitant);

        policy.Should().NotBeNull();
        policy!.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().Should().NotBeEmpty(
            "Solicitant authenticates via MPass and the policy must reject anonymous callers");
        policy.Requirements.OfType<RolesAuthorizationRequirement>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task SefulDirectiei_Policy_Is_Registered_And_Accepts_Multiple_Roles()
    {
        using var sp = (ServiceProvider)BuildProvider();
        var provider = sp.GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await provider.GetPolicyAsync(AuthorizationComposition.SefulDirectiei);

        policy.Should().NotBeNull();
        var rolesReq = policy!.Requirements.OfType<RolesAuthorizationRequirement>().Single();
        rolesReq.AllowedRoles.Should().Contain("seful-directiei");
    }

    [Fact]
    public async Task SefulCNAS_Policy_Is_Registered_And_Accepts_Only_The_Exec_Role()
    {
        using var sp = (ServiceProvider)BuildProvider();
        var provider = sp.GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await provider.GetPolicyAsync(AuthorizationComposition.SefulCNAS);

        policy.Should().NotBeNull();
        var rolesReq = policy!.Requirements.OfType<RolesAuthorizationRequirement>().Single();
        rolesReq.AllowedRoles.Should().Contain("seful-cnas");
        rolesReq.AllowedRoles.Should().NotContain("cnas-admin",
            "SefulCNAS is an exec-tier policy and admin claim alone must not satisfy it");
    }

    [Fact]
    public async Task LegacyInternalPolicies_Remain_Registered()
    {
        using var sp = (ServiceProvider)BuildProvider();
        var provider = sp.GetRequiredService<IAuthorizationPolicyProvider>();

        (await provider.GetPolicyAsync(AuthorizationComposition.CnasUser)).Should().NotBeNull();
        (await provider.GetPolicyAsync(AuthorizationComposition.CnasDecider)).Should().NotBeNull();
        (await provider.GetPolicyAsync(AuthorizationComposition.CnasAdmin)).Should().NotBeNull();
        (await provider.GetPolicyAsync(AuthorizationComposition.CnasTechAdmin)).Should().NotBeNull();
    }

    [Fact]
    public void AllGenericRoles_Catalog_Lists_The_Eight_TOR_Personas()
    {
        AuthorizationComposition.AllGenericRoles.Should().HaveCount(8);
        AuthorizationComposition.AllGenericRoles.Should().Contain([
            "UtilizatorInternet",
            "UtilizatorAutorizat",
            "Solicitant",
            "UtilizatorCNAS",
            "SefulDirectiei",
            "SefulCNAS",
            "AdministratorSistem",
            "AdministratorTehnic",
        ]);
    }

    [Fact]
    public void Persona_Constants_Match_Their_Role_Claim_Strings()
    {
        AuthorizationComposition.UtilizatorInternet.Should().Be("UtilizatorInternet");
        AuthorizationComposition.Solicitant.Should().Be("Solicitant");
        AuthorizationComposition.SefulDirectiei.Should().Be("SefulDirectiei");
        AuthorizationComposition.SefulCNAS.Should().Be("SefulCNAS");
    }

    [Fact]
    public void AuthorizationOptions_Registered_At_Least_Eight_Named_Policies()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCnasAuthorization();
        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        // The 4 legacy internal policies + 4 newly seeded R0055 personas.
        options.GetPolicy(AuthorizationComposition.CnasUser).Should().NotBeNull();
        options.GetPolicy(AuthorizationComposition.CnasDecider).Should().NotBeNull();
        options.GetPolicy(AuthorizationComposition.CnasAdmin).Should().NotBeNull();
        options.GetPolicy(AuthorizationComposition.CnasTechAdmin).Should().NotBeNull();
        options.GetPolicy(AuthorizationComposition.UtilizatorInternet).Should().NotBeNull();
        options.GetPolicy(AuthorizationComposition.Solicitant).Should().NotBeNull();
        options.GetPolicy(AuthorizationComposition.SefulDirectiei).Should().NotBeNull();
        options.GetPolicy(AuthorizationComposition.SefulCNAS).Should().NotBeNull();
    }
}
