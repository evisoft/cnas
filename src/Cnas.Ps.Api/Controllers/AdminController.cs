using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// Operations admin REST surface — restricted to the
/// <see cref="AuthorizationComposition.CnasTechAdmin"/> policy (UC20, technical
/// administrator). Current sub-resource: the failed-job dead-letter queue
/// (<see cref="IFailedJobStore"/>, CLAUDE.md §6.2). Additional ops sub-resources can be
/// added under this controller as they materialise without re-litigating the auth /
/// rate-limit contract.
/// </summary>
/// <param name="failedJobs">DLQ persistence + replay façade.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasTechAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin")]
public sealed class AdminController(IFailedJobStore failedJobs) : ControllerBase
{
    private readonly IFailedJobStore _failedJobs = failedJobs;

    /// <summary>
    /// Pages the failed-jobs DLQ ordered by <see cref="FailedJobOutput.FailedAtUtc"/>
    /// descending (newest first). Optional filters narrow by Quartz job name and/or
    /// minimum failure time so an operator can triage a specific incident window
    /// without scrolling.
    /// </summary>
    /// <param name="jobName">When supplied, restricts the page to DLQ rows matching this Quartz job name (e.g. <c>mpay-dispatcher</c>).</param>
    /// <param name="since">When supplied, restricts the page to DLQ rows with <c>FailedAtUtc &gt;= since</c>.</param>
    /// <param name="page">1-based page number. Defaults to 1.</param>
    /// <param name="pageSize">Items per page. Defaults to 20 (the store clamps to [1, 200]).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with a paged list of DLQ rows; 400 ProblemDetails on store failure.</returns>
    [HttpGet("failed-jobs")]
    public async Task<ActionResult<PagedResult<FailedJobOutput>>> ListFailedJobsAsync(
        [FromQuery] string? jobName = null,
        [FromQuery] DateTime? since = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _failedJobs
            .QueryAsync(jobName, since, new PageRequest(page, pageSize), cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<PagedResult<FailedJobOutput>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Schedules a one-shot Quartz replay of the DLQ entry identified by <paramref name="sqid"/>.
    /// The store decodes the Sqid, re-hydrates the original (PII-scrubbed) job data map, and
    /// fires the original job key immediately. On success the DLQ row is stamped with
    /// <c>ReplayState=scheduled</c> and <c>LastReplayAtUtc</c>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the DLQ entry to replay (RULE 3 — the raw <c>long</c> primary key is never exposed externally).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 204 No Content on success; 404 when the Sqid resolves to no row; 400 ProblemDetails
    /// when the Sqid is malformed or the underlying Quartz job is no longer registered.
    /// </returns>
    [HttpPost("failed-jobs/{sqid}/replay")]
    public async Task<IActionResult> ReplayFailedJobAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _failedJobs.ReplayAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a <see cref="Result{T}"/> failure to an <see cref="ActionResult{T}"/>.</summary>
    /// <typeparam name="T">The DTO type the action would have returned on success.</typeparam>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 400 ProblemDetails as appropriate.</returns>
    private ActionResult<T> MapFailureGeneric<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Maps a non-generic <see cref="Result"/> failure to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 400 ProblemDetails as appropriate.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>404 NotFound, or 400 BadRequest for everything else.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
