using System.Security.Claims;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0160 / R0161 / R0501 / R0520 / TOR CF 03.01 / CF 03.03 / CF 01.04 —
/// cross-domain search REST surface. Hosts three actions:
/// (1) the legacy <c>GET /api/search</c> five-domain full-text search;
/// (2) the metadata-driven criteria endpoint
/// <c>GET /api/search/criteria/{domain}</c> (R0501);
/// (3) the unified nine-domain search endpoint
/// <c>GET /api/search/unified</c> (R0520).
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation.</b> Every action is gated by
/// <see cref="AuthorizationComposition.CnasUser"/> and the authenticated
/// rate-limit policy. The unified endpoint additionally applies row-level
/// scoping inside the service via <c>ISearchRowLevelFilter</c> (R0526 / CF
/// 03.10).
/// </para>
/// </remarks>
/// <param name="svc">Underlying legacy full-text-search service.</param>
/// <param name="unified">Underlying unified cross-entity search service.</param>
/// <param name="catalog">Per-domain search-criteria catalogue.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasUser)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/search")]
public sealed class GlobalSearchController(
    IGlobalSearchService svc,
    IUnifiedDataSearchService unified,
    ISearchCriteriaCatalog catalog) : ControllerBase
{
    private readonly IGlobalSearchService _svc = svc;
    private readonly IUnifiedDataSearchService _unified = unified;
    private readonly ISearchCriteriaCatalog _catalog = catalog;

    /// <summary>
    /// Runs a cross-domain full-text search. The <c>q</c> parameter is required;
    /// <c>domains</c> may be a comma-separated subset of the canonical codes
    /// (empty / omitted = all domains). <c>skip</c> defaults to 0; <c>take</c>
    /// defaults to 20 and is capped at 100 server-side.
    /// </summary>
    /// <param name="q">Free-text query (required).</param>
    /// <param name="domains">
    /// Optional comma-separated domain list (e.g. <c>"applications,contributors"</c>).
    /// </param>
    /// <param name="skip">Zero-based skip (paging).</param>
    /// <param name="take">Page size (≤ 100).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with <see cref="GlobalSearchResultDto"/>; 400 on validation failure.</returns>
    [HttpGet]
    public async Task<IActionResult> SearchAsync(
        [FromQuery] string? q,
        [FromQuery] string? domains,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Problem(
                "Query parameter 'q' is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var domainList = ParseDomains(domains);
        var input = new GlobalSearchInputDto(q, domainList, skip, take);
        var result = await _svc.SearchAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0501 / TOR CF 01.04 — returns the metadata-driven criteria descriptors
    /// for the supplied domain. The UI consumes the list to render a generic
    /// query-by-example form without hard-coding per-domain field lists.
    /// </summary>
    /// <param name="domain">Stable lower-kebab-case domain code (e.g. <c>"applications"</c>).</param>
    /// <returns>
    /// 200 with the descriptor list when the domain is known; 404 ProblemDetails
    /// when no descriptors exist for the domain.
    /// </returns>
    [HttpGet("criteria/{domain}")]
    public Task<IActionResult> GetCriteriaAsync([FromRoute] string domain)
    {
        var descriptors = _catalog.GetCriteriaFor(domain);
        if (descriptors.Count == 0)
        {
            return Task.FromResult<IActionResult>(Problem(
                $"No search criteria are defined for domain '{domain}'.",
                statusCode: StatusCodes.Status404NotFound));
        }
        return Task.FromResult<IActionResult>(Ok(descriptors));
    }

    /// <summary>
    /// R0520 / TOR CF 03.01 — runs the unified cross-entity search across the
    /// nine canonical domains and returns a homogeneous projection. Row-level
    /// scoping is applied inside the service (R0526) per the calling
    /// principal.
    /// </summary>
    /// <param name="q">Free-text query (required).</param>
    /// <param name="domains">Optional comma-separated unified-domain list.</param>
    /// <param name="skip">Zero-based skip (paging).</param>
    /// <param name="take">Page size (≤ 100).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with <see cref="UnifiedSearchResult"/>; 400 on validation failure.</returns>
    [HttpGet("unified")]
    public async Task<IActionResult> UnifiedAsync(
        [FromQuery] string? q,
        [FromQuery] string? domains,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Problem(
                "Query parameter 'q' is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        var domainList = ParseDomains(domains);
        var input = new UnifiedSearchInput(q, domainList, skip, take);
        var user = HttpContext.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        var result = await _unified.SearchAsync(input, user, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Splits the optional <c>domains</c> query string into a trimmed,
    /// non-empty list. Returns <see langword="null"/> when nothing was supplied
    /// — signalling "search all domains" to the service.
    /// </summary>
    /// <param name="raw">Raw query string value.</param>
    /// <returns>Parsed list, or null when empty.</returns>
    private static IReadOnlyList<string>? ParseDomains(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts;
    }

    /// <summary>Maps a Result failure to an ActionResult with the appropriate status code.</summary>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>The mapped ProblemDetails action result.</returns>
    private IActionResult MapFailure(string? code, string? message)
    {
        var status = code switch
        {
            ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest,
        };
        return Problem(message, statusCode: status);
    }
}
