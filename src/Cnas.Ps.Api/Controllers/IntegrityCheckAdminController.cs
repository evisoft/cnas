using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Integrity;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2282 / TOR SEC 036 — admin REST surface over the row-integrity check
/// subsystem. Restricted to the <see cref="AuthorizationComposition.CnasAdmin"/>
/// policy because integrity findings expose internal aggregate state.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST /api/admin/integrity-check/runs</c> — start a manual run.</item>
///   <item><c>GET  /api/admin/integrity-check/runs/{sqid}</c> — run summary.</item>
///   <item><c>GET  /api/admin/integrity-check/runs/{sqid}/details</c> — run + findings.</item>
///   <item><c>GET  /api/admin/integrity-check/runs?take=50</c> — list recent runs.</item>
///   <item><c>GET  /api/admin/integrity-check/findings?...</c> — list open findings.</item>
///   <item><c>POST /api/admin/integrity-check/findings/{sqid}/acknowledge</c> — acknowledge.</item>
/// </list>
/// </para>
/// </remarks>
/// <param name="service">Integrity-check service façade.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/integrity-check")]
public sealed class IntegrityCheckAdminController(IIntegrityCheckService service) : ControllerBase
{
    private readonly IIntegrityCheckService _service = service;

    /// <summary>
    /// Triggers a fresh integrity-check run synchronously. Returns the
    /// completed run summary.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the completed <see cref="IntegrityCheckRunDto"/>.</returns>
    [HttpPost("runs")]
    public async Task<ActionResult<IntegrityCheckRunDto>> StartRunAsync(CancellationToken cancellationToken = default)
    {
        var result = await _service.StartManualRunAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<IntegrityCheckRunDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Fetches a single run by its Sqid (no findings attached).
    /// </summary>
    /// <param name="sqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 / 400 / 404.</returns>
    [HttpGet("runs/{sqid}")]
    public async Task<ActionResult<IntegrityCheckRunDto>> GetRunAsync(string sqid, CancellationToken cancellationToken = default)
    {
        var result = await _service.GetRunByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<IntegrityCheckRunDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Fetches a single run with its full findings list.
    /// </summary>
    /// <param name="sqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 / 400 / 404.</returns>
    [HttpGet("runs/{sqid}/details")]
    public async Task<ActionResult<IntegrityCheckRunDetailsDto>> GetRunDetailsAsync(string sqid, CancellationToken cancellationToken = default)
    {
        var result = await _service.GetRunDetailsAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<IntegrityCheckRunDetailsDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Lists the most-recent integrity-check runs.
    /// </summary>
    /// <param name="take">Number of rows to return (1..100; default 25).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the ordered list, or 400 on validation failure.</returns>
    [HttpGet("runs")]
    public async Task<ActionResult<System.Collections.Generic.IReadOnlyList<IntegrityCheckRunDto>>> ListRecentRunsAsync(
        [FromQuery] int take = 25,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ListRecentRunsAsync(take, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<System.Collections.Generic.IReadOnlyList<IntegrityCheckRunDto>>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Lists open (un-acknowledged) findings filtered by severity / aggregate
    /// / check code.
    /// </summary>
    /// <param name="severity">Optional severity filter.</param>
    /// <param name="aggregateName">Optional aggregate-name filter.</param>
    /// <param name="checkCode">Optional check-code filter.</param>
    /// <param name="onlyOpen">When true (default), returns only un-acknowledged rows.</param>
    /// <param name="skip">Page offset (≥ 0; default 0).</param>
    /// <param name="take">Page size (1..200; default 50).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the page, or 400 on validation failure.</returns>
    [HttpGet("findings")]
    public async Task<ActionResult<IntegrityFindingPageDto>> ListFindingsAsync(
        [FromQuery] string? severity = null,
        [FromQuery] string? aggregateName = null,
        [FromQuery] string? checkCode = null,
        [FromQuery] bool onlyOpen = true,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new IntegrityFindingFilterDto(
            Severity: severity,
            AggregateName: aggregateName,
            CheckCode: checkCode,
            OnlyOpen: onlyOpen,
            Skip: skip,
            Take: take);
        var result = await _service.ListOpenFindingsAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<IntegrityFindingPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Acknowledges a finding with an operator-supplied note.
    /// </summary>
    /// <param name="sqid">Sqid-encoded finding id.</param>
    /// <param name="input">Acknowledgement payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO, or 400 / 404 / 409.</returns>
    [HttpPost("findings/{sqid}/acknowledge")]
    [Consumes("application/json")]
    public async Task<ActionResult<IntegrityCheckFindingDto>> AcknowledgeFindingAsync(
        string sqid,
        [FromBody] IntegrityFindingAcknowledgeInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.AcknowledgeFindingAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<IntegrityCheckFindingDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Translates a failed <see cref="Result{T}"/> into the appropriate
    /// <see cref="ActionResult"/>: <c>INVALID_SQID</c> / <c>VALIDATION_FAILED</c>
    /// → 400, <c>NOT_FOUND</c> → 404, <c>CONFLICT</c> → 409, anything else
    /// → 500.
    /// </summary>
    /// <typeparam name="T">DTO type that would have been returned on success.</typeparam>
    /// <param name="errorCode">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="errorMessage">Human-readable description.</param>
    /// <returns>An <see cref="ActionResult{T}"/> carrying the appropriate HTTP status.</returns>
    private ActionResult<T> MapFailure<T>(string errorCode, string errorMessage)
        => errorCode switch
        {
            ErrorCodes.InvalidSqid => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.ValidationFailed => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
