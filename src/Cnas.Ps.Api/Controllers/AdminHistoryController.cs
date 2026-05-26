using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0191 / TOR SEC 050 / TOR ARH 028 — admin REST surface over the
/// application-level entity-history projection. Restricted to
/// <see cref="AuthorizationComposition.CnasAdmin"/> so only administrators can
/// read point-in-time snapshots — the payloads carry redacted but still
/// internally-sensitive entity columns.
/// </summary>
/// <param name="service">Entity-history read façade.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/history")]
public sealed class AdminHistoryController(IEntityHistoryService service) : ControllerBase
{
    private readonly IEntityHistoryService _service = service;

    /// <summary>
    /// Fetches the most-recent history snapshots for one tracked entity.
    /// </summary>
    /// <param name="type">CLR type name of the tracked entity (e.g. <c>UserProfile</c>).</param>
    /// <param name="id">Sqid-encoded id of the tracked entity.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with <see cref="EntityHistoryTimelineDto"/> on success; 400 when
    /// the Sqid cannot be decoded; 404 when the entity type is missing.
    /// </returns>
    [HttpGet]
    public async Task<ActionResult<EntityHistoryTimelineDto>> GetAsync(
        [FromQuery] string type,
        [FromQuery] string id,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetHistoryAsync(type, id, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        var status = result.ErrorCode switch
        {
            ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest,
        };
        return Problem(result.ErrorMessage, statusCode: status);
    }
}
