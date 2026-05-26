using System;
using System.Collections.Generic;
using System.Security.Claims;

namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// Bound configuration for the MPass SAML 2.0 sign-in flow. Replaces the OIDC-shaped
/// <c>MPass*</c> properties on <see cref="MGovOptions"/> once the live middleware swap
/// happens (see <c>docs/EGOV-INTEGRATION-GAP.md</c> §MPass) — for the current
/// preparation phase the options are consumed only by
/// <c>ISamlAssertionParser</c> and the ACS callback controller.
/// </summary>
/// <remarks>
/// <para>
/// The default <see cref="AttributeMap"/> reflects the canonical MEGA MPass attribute
/// vocabulary. Operators can extend it via configuration to surface additional
/// attributes the deployment cares about (e.g. <c>urn:egov.md/mpass/organization</c>)
/// without recompiling.
/// </para>
/// <para>
/// The <see cref="ClockSkew"/> is intentionally symmetric — both edges of the assertion
/// validity window (<c>NotBefore</c> and <c>NotOnOrAfter</c>) are widened by the same
/// tolerance so a slightly-fast IdP clock and a slightly-slow IdP clock are both
/// handled identically.
/// </para>
/// </remarks>
public sealed class MPassSamlOptions
{
    /// <summary>Configuration-section name (binds via <c>IConfiguration.GetSection</c>).</summary>
    public const string SectionName = "Cnas:MGov:MPassSaml";

    /// <summary>
    /// SAML 2.0 issuer URL — the public endpoint of the MPass IdP for the current
    /// environment. Examples: <c>https://mpass.staging.egov.md</c> (staging),
    /// <c>https://mpass.gov.md</c> (production).
    /// </summary>
    public string IssuerUrl { get; set; } = string.Empty;

    /// <summary>
    /// The CNAS service-provider entity id registered with MEGA. This value MUST match
    /// the <c>&lt;saml:Audience&gt;</c> element inside the assertion's
    /// <c>&lt;AudienceRestriction&gt;</c>; otherwise the parser rejects the assertion
    /// with <see cref="Cnas.Ps.Core.Common.ErrorCodes.SamlAssertionAudienceMismatch"/>.
    /// Typical value: <c>https://cnas.gov.md/sp</c>.
    /// </summary>
    public string ServiceProviderEntityId { get; set; } = string.Empty;

    /// <summary>
    /// Allowed clock skew applied symmetrically when validating <c>NotBefore</c> and
    /// <c>NotOnOrAfter</c> on assertions. Defaults to 5 minutes — wide enough to absorb
    /// realistic NTP drift between two cloud-hosted services without leaving the
    /// security window dangerously open.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Test-only escape hatch for parser unit tests and staging diagnostics. Production
    /// deployments must leave this at the secure default (<c>false</c>) so unsigned
    /// assertions never create an authenticated principal.
    /// </summary>
    public bool AllowUnsignedAssertionsForTesting { get; set; }

    /// <summary>
    /// Map from SAML <c>&lt;Attribute Name="..."&gt;</c> values to outgoing claim types.
    /// Unknown attribute names are silently dropped — the parser logs them at Debug so
    /// operators can audit deployments where MPass starts emitting additional fields.
    /// </summary>
    /// <remarks>
    /// The defaults cover IDNP, full name, email, role, and the two MPower delegation
    /// claims (principal IDNP + delegation id). The dictionary is created with the
    /// case-insensitive comparer so deployments that emit attribute names with a
    /// different casing still resolve.
    /// </remarks>
    public Dictionary<string, string> AttributeMap { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["urn:egov.md/mpass/idnp"] = "idnp",
        ["urn:egov.md/mpass/full_name"] = ClaimTypes.Name,
        ["urn:egov.md/mpass/email"] = ClaimTypes.Email,
        ["urn:egov.md/mpass/role"] = ClaimTypes.Role,
        ["urn:egov.md/mpower/principal_idnp"] = "mpower:principal_idnp",
        ["urn:egov.md/mpower/delegation_id"] = "mpower:delegation_id",
    };
}
