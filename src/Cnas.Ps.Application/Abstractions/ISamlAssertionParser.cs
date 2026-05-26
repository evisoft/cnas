using System.Security.Claims;
using System.Threading;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// Parses a SAML 2.0 assertion XML payload into a <see cref="ClaimsPrincipal"/>. The
/// interface lives in the Application layer so it can be consumed by both the API
/// (the future ACS controller) and by future tooling (e.g. an admin command that
/// inspects a captured assertion). The implementation lives in Infrastructure because
/// it depends on configuration bound through <c>IOptions</c>.
/// </summary>
/// <remarks>
/// <para>
/// This abstraction is part of the MPass OIDC -&gt; SAML migration foundation
/// (<c>docs/EGOV-INTEGRATION-GAP.md</c> §MPass). Until the live middleware swap is
/// performed, the parser is exercised by the prep-phase ACS controller
/// (<c>MPassSamlController</c>) which logs a parsed-OK summary without issuing a
/// cookie. The actual <c>AddSaml2(...)</c> + cookie wiring is deferred to a separate
/// epic.
/// </para>
/// <para>
/// <b>Signature validation is out of scope for the prep phase.</b> The implementation
/// does NOT verify the XMLDSig <c>&lt;Signature&gt;</c> element — staging cert
/// provisioning via semnatura.md is still pending. Treat <see cref="Parse"/> success
/// as "the assertion is structurally valid and within its validity window for this
/// audience" rather than "the assertion is cryptographically authentic".
/// </para>
/// </remarks>
public interface ISamlAssertionParser
{
    /// <summary>
    /// Parses a SAML 2.0 assertion XML document into a <see cref="ClaimsPrincipal"/>.
    /// The mapping from SAML attributes to claims uses the configured attribute map;
    /// multi-valued attributes (e.g. role) emit one claim per <c>AttributeValue</c>.
    /// The returned principal carries the supplied <paramref name="authenticationScheme"/>
    /// on its <see cref="ClaimsIdentity"/> so downstream middleware can attribute the
    /// claims back to MPass SAML.
    /// </summary>
    /// <param name="assertionXml">
    /// The SAML assertion XML — the inner <c>&lt;saml:Assertion&gt;</c> element after
    /// any transport-layer base64 decoding has already been performed by the caller.
    /// Must not be <c>null</c>.
    /// </param>
    /// <param name="authenticationScheme">
    /// The ASP.NET Core authentication scheme name to attach to the resulting
    /// <see cref="ClaimsIdentity"/> (e.g. <c>"MPassSaml"</c>). The value is
    /// propagated verbatim to <see cref="ClaimsIdentity.AuthenticationType"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token honoured by the caller.</param>
    /// <returns>
    /// On success: a <see cref="ClaimsPrincipal"/> built from the configured attribute
    /// map. On failure, the <see cref="Result{T}.ErrorCode"/> is one of
    /// <list type="bullet">
    ///   <item><see cref="ErrorCodes.InvalidSaml"/> — the XML cannot be parsed or a required structural piece (Conditions, AttributeStatement) is missing.</item>
    ///   <item><see cref="ErrorCodes.SamlAssertionExpired"/> — the validity window (<c>NotBefore</c> / <c>NotOnOrAfter</c>) does not include the current UTC instant (after clock-skew tolerance).</item>
    ///   <item><see cref="ErrorCodes.SamlAssertionAudienceMismatch"/> — the assertion's <c>AudienceRestriction</c> does not list the configured CNAS service-provider entity id, or the audience restriction is missing entirely.</item>
    /// </list>
    /// </returns>
    Result<ClaimsPrincipal> Parse(string assertionXml, string authenticationScheme, CancellationToken cancellationToken = default);
}
