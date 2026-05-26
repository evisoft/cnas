using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Contributors;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0302 / TOR §2.1 — read-only surface over the contributor source-system change
/// history. Only the per-contributor timeline is exposed via REST; writers
/// (<c>IContributorService</c>, the MConnect RSUD sync job) call the service
/// directly via DI.
/// </summary>
/// <param name="svc">Underlying history-service façade.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasDecider)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/contributors/{contributorSqid}/source-history")]
public sealed class ContributorSourceHistoryController(IContributorSourceHistoryService svc)
    : ControllerBase
{
    private readonly IContributorSourceHistoryService _svc = svc;

    /// <summary>
    /// Returns a page of source-change history rows for the supplied contributor.
    /// </summary>
    /// <param name="contributorSqid">Sqid-encoded id of the parent contributor.</param>
    /// <param name="skip">0-based offset (≥ 0).</param>
    /// <param name="take">Page size, clamped to <c>1..200</c>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with the page; 400 ProblemDetails on invalid Sqid; 404 when no contributor matches.
    /// </returns>
    [HttpGet]
    public async Task<ActionResult<ContributorSourceChangeHistoryPageDto>> ListAsync(
        [FromRoute] string contributorSqid,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.GetHistoryAsync(contributorSqid, skip, take, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }
        return result.ErrorCode == ErrorCodes.NotFound
            ? NotFound()
            : Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }
}
