using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2135 / TOR §15.2 ARH 026 — XSD export REST surface. Generates an XML Schema
/// (XSD) document on demand for any DTO in the curated allow-list so external
/// integrators (and the data-model glossary) have a machine-readable contract
/// to consume.
/// </summary>
/// <remarks>
/// <para>
/// Route table:
/// <list type="bullet">
///   <item><c>GET /api/admin/xsd</c> — listing of every DTO available through the portal (200).</item>
///   <item><c>GET /api/admin/xsd?dto={typeName}</c> — generated XSD 1.0 document for a DTO (200 / 404 / 400).</item>
/// </list>
/// </para>
/// <para>
/// <b>Authorization.</b> The portal is gated by <see cref="AuthorizationComposition.CnasTechAdmin"/>
/// — only the technical-admin persona can pull schema artefacts. Even though
/// XSDs are not strictly secret, the surface is treated as administrator-only
/// to avoid surfacing the DTO catalogue to unauthenticated clients.
/// </para>
/// </remarks>
/// <param name="exporter">Underlying XSD export service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasTechAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/xsd")]
public sealed class XsdExportController(IXsdExportService exporter) : ControllerBase
{
    private readonly IXsdExportService _exporter = exporter;

    /// <summary>
    /// Returns the generated XSD 1.0 document for the DTO identified by the
    /// <paramref name="dto"/> query-string parameter; when omitted, returns
    /// the listing of every DTO available through the portal.
    /// </summary>
    /// <param name="dto">
    /// Optional bare DTO type name (case-insensitive). When supplied, the
    /// response body is the XSD document with <c>application/xml</c>
    /// content-type. When omitted, the body is the JSON listing of supported
    /// DTO names.
    /// </param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with the XSD body (or listing); 400 on validation failure;
    /// 404 when the requested DTO is not registered.
    /// </returns>
    [HttpGet]
    public async Task<IActionResult> GetAsync(
        [FromQuery] string? dto = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dto))
        {
            var listing = await _exporter.ListAsync(cancellationToken).ConfigureAwait(false);
            return listing.IsSuccess
                ? Ok(listing.Value)
                : Problem(listing.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _exporter.ExportAsync(dto, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.ErrorCode switch
            {
                ErrorCodes.NotFound => NotFound(),
                ErrorCodes.ValidationFailed =>
                    Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest),
                _ => Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest),
            };
        }
        return Content(result.Value, "application/xml");
    }
}
