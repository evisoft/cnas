using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Recalculation;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R1503 / TOR §3.7-D — admin REST surface over the mass-recalculation
/// engine. Restricted to the <see cref="AuthorizationComposition.CnasAdmin"/>
/// policy because mass-recalculation runs touch every active benefit
/// decision in scope.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST /api/admin/mass-recalculation/{legalChangeSqid}/dry-run</c>.</item>
///   <item><c>POST /api/admin/mass-recalculation/{legalChangeSqid}/apply</c>.</item>
///   <item><c>GET  /api/admin/mass-recalculation/runs/{sqid}</c>.</item>
///   <item><c>GET  /api/admin/mass-recalculation/runs/{sqid}/details</c>.</item>
///   <item><c>GET  /api/admin/mass-recalculation/runs</c>.</item>
///   <item><c>POST /api/admin/mass-recalculation/results/{sqid}/reject</c>.</item>
///   <item><c>POST /api/admin/mass-recalculation/runs/{sqid}/apply-approved</c>.</item>
/// </list>
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/mass-recalculation")]
public sealed class MassRecalculationAdminController : ControllerBase
{
    private readonly IMassRecalculationService _service;

    /// <summary>Constructs the controller.</summary>
    /// <param name="service">Mass-recalculation service façade.</param>
    public MassRecalculationAdminController(IMassRecalculationService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <summary>Starts a DryRun against the supplied legal-change event.</summary>
    /// <param name="legalChangeSqid">Sqid-encoded event id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 / 400 / 404 / 409.</returns>
    [HttpPost("{legalChangeSqid}/dry-run")]
    public async Task<ActionResult<RecalculationRunDto>> StartDryRunAsync(
        string legalChangeSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.StartDryRunAsync(legalChangeSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<RecalculationRunDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Starts an Apply run against the supplied legal-change event.</summary>
    /// <param name="legalChangeSqid">Sqid-encoded event id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 / 400 / 404 / 409.</returns>
    [HttpPost("{legalChangeSqid}/apply")]
    public async Task<ActionResult<RecalculationRunDto>> StartApplyAsync(
        string legalChangeSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.StartApplyAsync(legalChangeSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<RecalculationRunDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Fetches a single run by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 / 400 / 404.</returns>
    [HttpGet("runs/{sqid}")]
    public async Task<ActionResult<RecalculationRunDto>> GetRunAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetRunByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<RecalculationRunDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Fetches a run with its filtered, paged decision-results list.</summary>
    /// <param name="sqid">Sqid-encoded run id.</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="skip">Page offset (default 0).</param>
    /// <param name="take">Page size (default 50; max 200).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the envelope; 400 / 404 on failure.</returns>
    [HttpGet("runs/{sqid}/details")]
    public async Task<ActionResult<RecalculationRunDetailsDto>> GetRunDetailsAsync(
        string sqid,
        [FromQuery] string? status = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new RecalculationResultFilterDto(Status: status, Skip: skip, Take: take);
        var result = await _service.GetRunDetailsAsync(sqid, filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<RecalculationRunDetailsDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists recent mass-recalculation runs.</summary>
    /// <param name="mode">Optional mode filter.</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="legalChangeSqid">Optional parent event filter.</param>
    /// <param name="skip">Page offset (default 0).</param>
    /// <param name="take">Page size (default 50; max 100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the page DTO.</returns>
    [HttpGet("runs")]
    public async Task<ActionResult<RecalculationRunPageDto>> ListRunsAsync(
        [FromQuery] string? mode = null,
        [FromQuery] string? status = null,
        [FromQuery] string? legalChangeSqid = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new RecalculationRunFilterDto(
            Mode: mode,
            Status: status,
            LegalChangeSqid: legalChangeSqid,
            Skip: skip,
            Take: take);
        var result = await _service.ListRunsAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<RecalculationRunPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Rejects a Computed result row with an operator rationale.</summary>
    /// <param name="sqid">Sqid-encoded result id.</param>
    /// <param name="input">Reject payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 / 400 / 404 / 409.</returns>
    [HttpPost("results/{sqid}/reject")]
    [Consumes("application/json")]
    public async Task<ActionResult<RecalculationDecisionResultDto>> RejectResultAsync(
        string sqid,
        [FromBody] RecalculationResultRejectInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.RejectResultAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<RecalculationDecisionResultDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Applies every non-rejected Computed result on the run.</summary>
    /// <param name="sqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated run DTO; 400 / 404 / 409 on failure.</returns>
    [HttpPost("runs/{sqid}/apply-approved")]
    public async Task<ActionResult<RecalculationRunDto>> ApplyApprovedAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ApplyApprovedResultsAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<RecalculationRunDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Maps a failed result to an HTTP status.</summary>
    /// <typeparam name="T">DTO type on success.</typeparam>
    /// <param name="errorCode">Stable code.</param>
    /// <param name="errorMessage">Description.</param>
    /// <returns>Action result with the mapped status.</returns>
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
