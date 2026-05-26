using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0165 / CF 03.06 — saved searches REST surface. Operators persist a saved query
/// (registry + filter + friendly name), reload it later, and optionally publish it to
/// every authenticated CNAS staff member by flipping the shared flag. Sharing is
/// unilateral — owners may always mutate their own rows; non-owners read only
/// <c>IsShared = true</c> rows and cannot mutate them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>GET    /api/saved-searches?registry=&lt;reg&gt;</c> — list owned + shared rows for a registry.</item>
///   <item><c>GET    /api/saved-searches/{sqid}</c>             — fetch one row.</item>
///   <item><c>POST   /api/saved-searches</c>                    — create a new row; idempotent on natural key.</item>
///   <item><c>PUT    /api/saved-searches/{sqid}</c>             — owner-only mutation of name / filter / shared.</item>
///   <item><c>DELETE /api/saved-searches/{sqid}</c>             — owner-only soft delete.</item>
/// </list>
/// </para>
/// <para>
/// <b>Sqid convention.</b> The <c>{sqid}</c> route segment is a Sqid-encoded id per
/// CLAUDE.md RULE 3. Malformed values surface as <see cref="ErrorCodes.InvalidSqid"/>
/// → 400.
/// </para>
/// <para>
/// <b>Error-code → HTTP status mapping.</b>
/// <see cref="ErrorCodes.NotFound"/> → 404,
/// <see cref="ErrorCodes.Forbidden"/> → 403,
/// <see cref="ErrorCodes.Unauthorized"/> → 401,
/// <see cref="ErrorCodes.ValidationFailed"/>, <see cref="ErrorCodes.InvalidSqid"/>,
/// <see cref="ErrorCodes.SavedSearchLimitReached"/> → 400.
/// </para>
/// </remarks>
/// <param name="svc">Underlying saved-search service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasUser)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/saved-searches")]
public sealed class SavedSearchesController(ISavedSearchService svc) : ControllerBase
{
    private readonly ISavedSearchService _svc = svc;

    /// <summary>
    /// Lists the caller's accessible saved searches for the supplied registry. The
    /// response is the unordered union of the caller's own rows (any sharing state) and
    /// every row another user has published with <c>IsShared = true</c>.
    /// </summary>
    /// <param name="registry">Registry code (e.g. <c>Contributors</c>); required.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with the list on success; 400 when the registry query parameter is missing or
    /// whitespace.
    /// </returns>
    [HttpGet]
    public async Task<IActionResult> ListAsync(
        [FromQuery] string? registry,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(registry))
        {
            // Short-circuit before the service so the caller gets a tight 400 rather
            // than a service-layer ValidationFailed wrapping the same condition.
            return Problem("Query parameter 'registry' is required.", statusCode: StatusCodes.Status400BadRequest);
        }
        var result = await _svc.ListAsync(registry, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Fetches a single saved search by Sqid. Owners always read their own rows;
    /// non-owners may only read rows the owner published with <c>IsShared = true</c>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the row; 403 when private to another owner; 404 when missing.</returns>
    [HttpGet("{sqid}")]
    public async Task<IActionResult> GetAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.GetAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Persists a new saved search owned by the caller. Idempotent on the
    /// <c>(OwnerUserId, Registry, Name)</c> natural key — a duplicate triple returns the
    /// existing row's Sqid without overwriting fields. Use <c>PUT</c> to mutate.
    /// </summary>
    /// <param name="input">Create payload (required body).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 201 with the Sqid id and a <c>Location</c> header pointing at the new row; 400 on
    /// validation failure or the per-owner cap; 401 when the caller is anonymous.
    /// </returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<IActionResult> CreateAsync(
        [FromBody] SavedSearchCreateInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _svc.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return MapFailureBare(result.ErrorCode, result.ErrorMessage);
        }

        // 201 Created with a Location header pointing at the GET endpoint. The shape
        // mirrors REST conventions so a SPA round-tripping the new id picks the URL out
        // of the response headers rather than hard-coding the route.
        var sqid = result.Value;
        return CreatedAtAction(nameof(GetAsync), new { sqid }, sqid);
    }

    /// <summary>
    /// Updates the three mutable fields (name, filter, shared flag) of a saved search
    /// owned by the caller. Non-owners with read access via <c>IsShared</c> receive 403.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id.</param>
    /// <param name="input">Update payload (required body).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 400 / 403 / 404 on failure.</returns>
    [HttpPut("{sqid}")]
    [Consumes("application/json")]
    public async Task<IActionResult> UpdateAsync(
        [FromRoute] string sqid,
        [FromBody] SavedSearchUpdateInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _svc.UpdateAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Soft-deletes a saved search owned by the caller (flips <c>IsActive = false</c>).
    /// Non-owners receive 403.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 403 when not the owner; 404 when missing.</returns>
    [HttpDelete("{sqid}")]
    public async Task<IActionResult> DeleteAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.DeleteAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0524 / TOR CF 03.06 — flips the sharing scope of a saved search the caller
    /// owns. Body carries the new scope (<c>Private</c> / <c>Shared</c> / <c>Group</c>)
    /// and the optional group code (required iff scope = Group, forbidden otherwise).
    /// Non-owners receive 403; missing rows 404; validation failures 400.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the row.</param>
    /// <param name="input">Share payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated <see cref="SavedSearchItem"/>; 400/403/404 on failure.</returns>
    [HttpPost("{sqid}/share")]
    [Consumes("application/json")]
    public async Task<IActionResult> ShareAsync(
        [FromRoute] string sqid,
        [FromBody] SavedSearchShareInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _svc.ShareAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0524 / TOR CF 03.06 — lists every saved search the caller can READ on the named
    /// registry: their own rows, every <c>Shared</c> row from other users, and every
    /// <c>Group</c> row whose group code is in the caller's <c>UserProfile.Groups</c>
    /// set. Sorted by <c>Name</c> ascending.
    /// </summary>
    /// <param name="registry">Registry code (e.g. <c>Contributors</c>); required.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the accessible rows; 400 when the registry query parameter is missing.</returns>
    [HttpGet("accessible")]
    public async Task<IActionResult> AccessibleAsync(
        [FromQuery] string? registry,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(registry))
        {
            return Problem("Query parameter 'registry' is required.", statusCode: StatusCodes.Status400BadRequest);
        }
        var rows = await _svc.ListAccessibleAsync(registry, cancellationToken).ConfigureAwait(false);
        return Ok(rows);
    }

    /// <summary>Maps a non-generic <see cref="Result"/> failure to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>The mapped ProblemDetails / NotFound action result.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>Mapped HTTP status code.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        ErrorCodes.SavedSearchLimitReached => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
