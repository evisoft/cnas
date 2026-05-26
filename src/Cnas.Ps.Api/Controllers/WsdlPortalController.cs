using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2164 / TOR §15.4 INT 005 — WSDL portal REST surface. Anonymous-accessible (the WSDL
/// surface is public metadata, exactly like OpenAPI) and rate-limited via the
/// <see cref="RateLimitingPolicies.Anonymous"/> policy.
/// </summary>
/// <remarks>
/// <para>
/// Route table:
/// <list type="bullet">
///   <item><c>GET /api/wsdl-portal</c> — landing-page listing every controller WSDL surface (200).</item>
///   <item><c>GET /api/wsdl-portal/{controllerName}.wsdl</c> — generated WSDL 1.1 document for a controller (200 / 404).</item>
/// </list>
/// </para>
/// <para>
/// The <c>.wsdl</c> suffix on the per-controller route is deliberate: legacy SOAP
/// tooling (wsimport, svcutil) routinely auto-appends or expects this extension on the
/// WSDL URL. Adding the suffix into the route keeps clients working without manual URL
/// fiddling.
/// </para>
/// </remarks>
/// <param name="portal">Underlying WSDL portal service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasTechAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/wsdl-portal")]
public sealed class WsdlPortalController(IWsdlPortalService portal) : ControllerBase
{
    private readonly IWsdlPortalService _portal = portal;

    /// <summary>
    /// Returns the WSDL portal landing-page listing — one row per controller exposed
    /// through the portal, ordered by controller name.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the listing; 400 on unexpected service failure.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WsdlListingDto>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _portal.ListAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Returns the generated WSDL 1.1 document for <paramref name="controllerName"/>.
    /// Content type is <c>application/wsdl+xml</c> with a UTF-8 charset; SOAP-stub
    /// tooling that sniffs the content type for routing recognises this directly.
    /// </summary>
    /// <param name="controllerName">Stable controller name (no <c>Controller</c> suffix, case-insensitive).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the WSDL body; 404 when the controller is unknown.</returns>
    [HttpGet("{controllerName}.wsdl")]
    [Produces("application/wsdl+xml")]
    public async Task<IActionResult> GetWsdlAsync(
        [FromRoute] string controllerName,
        CancellationToken cancellationToken = default)
    {
        var result = await _portal.GetForControllerAsync(controllerName, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.ErrorCode == ErrorCodes.NotFound
                ? NotFound()
                : Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }
        return Content(result.Value!.WsdlXml, "application/wsdl+xml");
    }
}
