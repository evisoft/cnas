using System;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// Assertion Consumer Service (ACS) endpoint for the future MEGA MPass SAML POST
/// binding. Hosted under <c>POST /api/saml/acs</c>, the controller receives a
/// form-urlencoded body with a single <c>SAMLResponse</c> field carrying the
/// base64-encoded SAML 2.0 assertion XML.
/// </summary>
/// <remarks>
/// <para>
/// <b>This endpoint is the future ACS for MEGA MPass SAML POST binding.</b> The
/// cookie-issuance path will be added when the live middleware swap is performed
/// (see <c>docs/EGOV-INTEGRATION-GAP.md</c> §MPass). For the current preparation
/// phase the controller does NOT issue a cookie or call <c>SignInAsync</c> — it
/// parses the assertion, logs a structured summary so operators can confirm
/// end-to-end connectivity with the MEGA staging IdP, and returns a JSON body of
/// the form <c>{ status: "parsed", claims: ... }</c>.
/// </para>
/// <para>
/// The endpoint is <see cref="AllowAnonymousAttribute"/> because the SAML POST is
/// the very mechanism by which authentication is established; the caller is the
/// MPass IdP itself, vouched for at the transport layer (mTLS / gateway IP
/// allow-list) rather than via an <c>Authorization</c> header. The future SAML
/// middleware will additionally validate the XMLDSig signature on the assertion —
/// see the TODO in <c>MPassSamlAssertionParser.Parse</c>.
/// </para>
/// </remarks>
/// <param name="parser">SAML assertion parser injected from the Infrastructure layer.</param>
/// <param name="logger">Structured logger; receives a summary of parsed claims, never the raw XML.</param>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingPolicies.Anonymous)]
[Route("api/saml")]
public sealed class MPassSamlController(
    ISamlAssertionParser parser,
    ILogger<MPassSamlController> logger) : ControllerBase
{
    private readonly ISamlAssertionParser _parser = parser;
    private readonly ILogger<MPassSamlController> _logger = logger;

    /// <summary>Authentication scheme name attached to the parsed principal.</summary>
    /// <remarks>
    /// Kept in sync with the future SAML middleware registration. The prep-phase
    /// endpoint does not consume the scheme — the value is recorded on the identity
    /// for traceability and for any downstream logging that wants to attribute the
    /// claims back to MPass SAML rather than to the legacy OIDC scheme.
    /// </remarks>
    public const string AuthenticationScheme = "MPassSaml";

    /// <summary>
    /// Receives an MPass SAML POST. Decodes the base64-encoded <c>SAMLResponse</c>
    /// form field, parses the assertion, and returns a JSON summary.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token honoured by the request pipeline.</param>
    /// <returns>
    /// <list type="bullet">
    ///   <item><c>200 OK</c> with <c>{ status, claims }</c> when the assertion parsed cleanly.</item>
    ///   <item><c>400 Bad Request</c> with an <c>{ errorCode, errorMessage }</c> body when the form field is missing, the value is not valid base64, or the parser rejected the assertion.</item>
    /// </list>
    /// </returns>
    [HttpPost("acs")]
    public async Task<IActionResult> AcsAsync(CancellationToken cancellationToken = default)
    {
        // The SAML POST binding always uses form-urlencoded — bail early if the body
        // lacks the field, with a clear 400 + descriptive message so operators wiring
        // MEGA staging can self-diagnose.
        if (!Request.HasFormContentType)
        {
            return Problem(
                detail: "SAMLResponse form field is required.",
                statusCode: 400,
                title: "missing SAMLResponse");
        }

        var form = await Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var samlResponse = form["SAMLResponse"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(samlResponse))
        {
            return Problem(
                detail: "SAMLResponse form field is required.",
                statusCode: 400,
                title: "missing SAMLResponse");
        }

        // Decode the base64 envelope. The SAML HTTP POST binding mandates base64; a
        // value that is not valid base64 is a protocol-level error and maps to
        // INVALID_SAML rather than to a generic 400.
        string assertionXml;
        try
        {
            var bytes = Convert.FromBase64String(samlResponse);
            assertionXml = Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return Problem(
                detail: "SAMLResponse is not valid base64.",
                statusCode: 400,
                title: ErrorCodes.InvalidSaml);
        }

        var parsed = _parser.Parse(assertionXml, AuthenticationScheme, cancellationToken);
        if (parsed.IsFailure)
        {
            return Problem(
                detail: parsed.ErrorMessage,
                statusCode: 400,
                title: parsed.ErrorCode);
        }

        // Build a structured summary for the response body and for the log entry. The
        // log fields match the fields the future cookie-issuance code will need
        // (subjectIdnp, principalIdnp, delegationId) so operators wiring MEGA staging
        // can pre-validate the attribute map before the live middleware swap.
        var principal = parsed.Value;
        var subjectIdnp = principal.FindFirst("idnp")?.Value;
        var principalIdnp = principal.FindFirst("mpower:principal_idnp")?.Value;
        var delegationId = principal.FindFirst("mpower:delegation_id")?.Value;

        // R0502 / SEC-AUDIT — never structured-log the raw IDNP fields. The
        // structured-log sink is operator-facing and not the audit trail (which
        // is the correct destination for sensitive identifiers, journalled by
        // IAuditService elsewhere in the request pipeline). We emit only
        // presence booleans plus the delegationId, which is a Sqid-shaped
        // opaque identifier rather than a national-identity number.
        _logger.LogInformation(
            "SAML assertion parsed successfully delegationId={DelegationId} hasPrincipal={HasPrincipal} hasSubject={HasSubject}.",
            delegationId,
            principalIdnp is not null,
            subjectIdnp is not null);

        var summary = new
        {
            status = "parsed",
            claimSummary = new
            {
                hasSubjectIdnp = subjectIdnp is not null,
                hasPrincipalIdnp = principalIdnp is not null,
                delegationId,
                hasName = principal.HasClaim(c => c.Type == ClaimTypes.Name),
                hasEmail = principal.HasClaim(c => c.Type == ClaimTypes.Email),
                roleCount = principal.FindAll(ClaimTypes.Role).Count(),
            },
        };
        return Ok(summary);
    }
}
