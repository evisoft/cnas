using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.ServiceManagement;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2504 / TOR PIR 024 — admin REST surface over the system-update event
/// registry. Restricted to
/// <see cref="AuthorizationComposition.CnasAdmin"/>.
/// </summary>
/// <param name="service">System-update event service façade.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/system-updates/events")]
public sealed class SystemUpdateEventsController(ISystemUpdateEventService service) : ControllerBase
{
    private readonly ISystemUpdateEventService _service = service;

    /// <summary>Creates a new event.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<SystemUpdateEventDto>> CreateAsync(
        [FromBody] SystemUpdateEventCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SystemUpdateEventDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Dispatches the public notice (Planned → Notified).</summary>
    /// <param name="sqid">Sqid-encoded event id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/notify")]
    public async Task<ActionResult<SystemUpdateEventDto>> NotifyAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.NotifyAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SystemUpdateEventDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Starts the deployment (Notified → Deploying).</summary>
    /// <param name="sqid">Sqid-encoded event id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/start")]
    public async Task<ActionResult<SystemUpdateEventDto>> StartAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.StartDeploymentAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SystemUpdateEventDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Completes the deployment (Deploying → Deployed).</summary>
    /// <param name="sqid">Sqid-encoded event id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/complete")]
    public async Task<ActionResult<SystemUpdateEventDto>> CompleteAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.CompleteDeploymentAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SystemUpdateEventDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Cancels the event.</summary>
    /// <param name="sqid">Sqid-encoded event id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/cancel")]
    [Consumes("application/json")]
    public async Task<ActionResult<SystemUpdateEventDto>> CancelAsync(
        string sqid,
        [FromBody] SystemUpdateEventReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CancelAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SystemUpdateEventDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets an event by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded event id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<SystemUpdateEventDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SystemUpdateEventDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists events matching the filter.</summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="scheduleSqid">Optional schedule filter.</param>
    /// <param name="skip">Page offset.</param>
    /// <param name="take">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the page on success.</returns>
    [HttpGet]
    public async Task<ActionResult<SystemUpdateEventPageDto>> ListAsync(
        [FromQuery] string? status = null,
        [FromQuery] string? scheduleSqid = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new SystemUpdateEventFilterDto(status, scheduleSqid, skip, take);
        var result = await _service.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SystemUpdateEventPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Translates a service failure into the appropriate HTTP response.</summary>
    /// <typeparam name="T">DTO type on success.</typeparam>
    /// <param name="errorCode">Stable error code.</param>
    /// <param name="errorMessage">Human-readable description.</param>
    /// <returns><see cref="ActionResult{T}"/> with the correct HTTP status.</returns>
    private ActionResult<T> MapFailure<T>(string errorCode, string errorMessage)
        => errorCode switch
        {
            ErrorCodes.InvalidSqid => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.ValidationFailed => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.UpdateEventInvalidTransition => Conflict(new { error = errorCode, message = errorMessage }),
            ErrorCodes.UpdateEventLeadTimeInsufficient => Conflict(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
