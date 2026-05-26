using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Reports;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0583 / TOR CF 09.06 / CF 09.09 — REST surface for the background report
/// runner. Authenticated callers enqueue executions of an existing
/// <c>ReportTemplate</c>, poll for status, cancel queued jobs, and list their
/// most recent runs. When a job reaches <c>Succeeded</c> the DTO carries the
/// Sqid of the produced <see cref="AttachmentRecordDto"/>; clients then call
/// <c>GET /api/attachments/{sqid}/download</c> to fetch the export bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST   /api/reports/jobs</c>            — enqueue a new background run (201).</item>
///   <item><c>GET    /api/reports/jobs</c>            — list the caller's recent jobs.</item>
///   <item><c>GET    /api/reports/jobs/{sqid}</c>     — fetch one job by id.</item>
///   <item><c>POST   /api/reports/jobs/{sqid}/cancel</c> — cancel a Queued job.</item>
/// </list>
/// </para>
/// <para>
/// <b>Authorisation.</b> Every endpoint requires
/// <see cref="AuthorizationComposition.CnasUser"/>; any authenticated CNAS
/// staff member may enqueue + manage their own jobs. The service layer
/// enforces requester-only access at the row level.
/// </para>
/// </remarks>
/// <param name="jobs">Underlying report-job service.</param>
/// <param name="sqids">Sqid encoder/decoder for route ids.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasUser)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/reports/jobs")]
public sealed class ReportJobsController(
    IReportJobService jobs,
    ISqidService sqids) : ControllerBase
{
    private readonly IReportJobService _jobs = jobs;
    private readonly ISqidService _sqids = sqids;

    /// <summary>Default page size for the list endpoint.</summary>
    public const int DefaultListTake = 20;

    /// <summary>
    /// Enqueues a new background report run. The job lands in <c>Queued</c>
    /// status; the runner picks it up on its next tick.
    /// </summary>
    /// <param name="input">Enqueue payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>201 with the new job DTO; 4xx on validation / forbidden / not-found.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<IActionResult> EnqueueAsync(
        [FromBody] ReportJobEnqueueDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _jobs.EnqueueAsync(input, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return MapFailureBare(result.ErrorCode, result.ErrorMessage);
        }
        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>Lists the caller's recent jobs (newest first).</summary>
    /// <param name="take">Maximum number of rows; clamped to <c>[1, 100]</c>; default 20.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the list.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ReportJobDto>>> ListAsync(
        [FromQuery] int take = DefaultListTake,
        CancellationToken cancellationToken = default)
    {
        var items = await _jobs.ListForCurrentUserAsync(take, cancellationToken).ConfigureAwait(false);
        return Ok(items);
    }

    /// <summary>Fetches one job by its Sqid.</summary>
    /// <param name="sqid">Sqid-encoded job id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the DTO; 4xx on bad-sqid / forbidden / not-found.</returns>
    [HttpGet("{sqid}")]
    public async Task<IActionResult> GetAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureBare(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var result = await _jobs.GetAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Cancels a Queued job.</summary>
    /// <param name="sqid">Sqid-encoded job id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 on success; 400 with <c>JOB_NOT_CANCELLABLE</c> when the row is not
    /// in <c>Queued</c> status; 404 when the row is missing.
    /// </returns>
    [HttpPost("{sqid}/cancel")]
    public async Task<IActionResult> CancelAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureBare(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var result = await _jobs.CancelAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok()
            : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Maps a non-generic <see cref="Result"/> failure to a ProblemDetails
    /// action result.
    /// </summary>
    /// <param name="code">Stable error code.</param>
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
        ErrorCodes.Conflict => StatusCodes.Status409Conflict,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
