using System;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abac;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Api.Authorization;

/// <summary>
/// R2271 / TOR SEC 025 — custom <see cref="IAuthorizationPolicyProvider"/>
/// that recognises the synthetic policy-name prefix
/// <see cref="AbacPolicyAttribute.PolicyNamePrefix"/> (<c>"abac:"</c>) and
/// builds an <see cref="AbacRequirement"/>-backed policy on demand. Any other
/// policy name falls through to the underlying default provider so existing
/// RBAC policies registered via
/// <c>AuthorizationComposition.AddCnasAuthorization</c> keep working unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registration.</b> Registered as a Singleton via the API composition root.
/// The MVC framework calls <see cref="GetPolicyAsync"/> once per cache-miss;
/// resulting policies are cached internally by the framework.
/// </para>
/// </remarks>
public sealed class AbacPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback;

    /// <summary>Constructs the provider with a delegated default provider.</summary>
    /// <param name="options">Bound authorization options.</param>
    public AbacPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _fallback = new DefaultAuthorizationPolicyProvider(options);
    }

    /// <inheritdoc />
    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    /// <inheritdoc />
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    /// <inheritdoc />
    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!string.IsNullOrEmpty(policyName)
            && policyName.StartsWith(AbacPolicyAttribute.PolicyNamePrefix, StringComparison.Ordinal))
        {
            var inner = policyName[AbacPolicyAttribute.PolicyNamePrefix.Length..];
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new AbacRequirement(inner))
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }
        return _fallback.GetPolicyAsync(policyName);
    }
}
