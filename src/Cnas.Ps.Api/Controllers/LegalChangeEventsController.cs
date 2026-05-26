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
/// R1503 / TOR §3.7-D — admin REST surface over the legal-change-events
/// registry. Restricted to the <see cref="AuthorizationComposition.CnasAdmin"/>
/// policy because legal-framework changes are high-trust events.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST /api/legal-change-events</c> — register.</item>
///   <item><c>PUT  /api/legal-change-events/{sqid}</c> — modify.</item>
///   <item><c>POST /api/legal-change-events/{sqid}/mark-ready</c> — flip to Ready.</item>
///   <item><c>POST /api/legal-change-events/{sqid}/cancel</c> — cancel.</item>
///   <item><c>GET  /api/legal-change-events/{sqid}</c> — get.</item>
///   <item><c>GET  /api/legal-change-events</c> — list.</item>
/// </list>
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/legal-change-events")]
public sealed class LegalChangeEventsController : ControllerBase
{
    private readonly ILegalChangeEventService _service;

    /// <summary>Constructs the controller.</summary>
    /// <param name="service">Legal-change-event service.</param>
    public LegalChangeEventsController(ILegalChangeEventService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <summary>Registers a new legal-change event.</summary>
    /// <param name="input">Register payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>201 + Location on success; 400/409 otherwise.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<LegalChangeEventDto>> RegisterAsync(
        [FromBody] LegalChangeEventRegisterInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.RegisterAsync(input, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return MapFailure<LegalChangeEventDto>(result.ErrorCode!, result.ErrorMessage!);
        }
        return CreatedAtAction(nameof(GetByIdAsync), new { sqid = result.Value.Id }, result.Value);
    }

    /// <summary>Modifies a Draft legal-change event.</summary>
    /// <param name="sqid">Sqid-encoded event id.</param>
    /// <param name="input">Modify payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 / 400 / 404 / 409.</returns>
    [HttpPut("{sqid}")]
    [Consumes("application/json")]
    public async Task<ActionResult<LegalChangeEventDto>> ModifyAsync(
        string sqid,
        [FromBody] LegalChangeEventModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ModifyAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<LegalChangeEventDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Flips a Draft event to Ready.</summary>
    /// <param name="sqid">Sqid-encoded event id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 / 400 / 404 / 409.</returns>
    [HttpPost("{sqid}/mark-ready")]
    public async Task<ActionResult<LegalChangeEventDto>> MarkReadyAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.MarkReadyAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<LegalChangeEventDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Cancels a non-Applied event with a rationale.</summary>
    /// <param name="sqid">Sqid-encoded event id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 / 400 / 404 / 409.</returns>
    [HttpPost("{sqid}/cancel")]
    [Consumes("application/json")]
    public async Task<ActionResult<LegalChangeEventDto>> CancelAsync(
        string sqid,
        [FromBody] LegalChangeEventReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CancelAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<LegalChangeEventDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Fetches a single legal-change event by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded event id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 / 400 / 404.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<LegalChangeEventDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<LegalChangeEventDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists legal-change events.</summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="scope">Optional scope filter.</param>
    /// <param name="effectiveFromAfter">Optional lower-bound on effective-from.</param>
    /// <param name="skip">Page offset (default 0).</param>
    /// <param name="take">Page size (default 50).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with paged DTO; 400 on validation failure.</returns>
    [HttpGet]
    public async Task<ActionResult<LegalChangeEventPageDto>> ListAsync(
        [FromQuery] string? status = null,
        [FromQuery] string? scope = null,
        [FromQuery] DateOnly? effectiveFromAfter = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new LegalChangeEventFilterDto(
            Status: status,
            Scope: scope,
            EffectiveFromAfter: effectiveFromAfter,
            Skip: skip,
            Take: take);
        var result = await _service.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<LegalChangeEventPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Maps a failed <see cref="Result{T}"/> into the appropriate HTTP status.
    /// </summary>
    /// <typeparam name="T">DTO type on success.</typeparam>
    /// <param name="errorCode">Stable code.</param>
    /// <param name="errorMessage">Human description.</param>
    /// <returns><see cref="ActionResult{T}"/> with the mapped status.</returns>
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
