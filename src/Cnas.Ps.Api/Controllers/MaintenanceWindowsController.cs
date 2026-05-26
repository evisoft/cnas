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
/// R2502 / TOR PIR 025 — admin REST surface over the maintenance-window
/// registry. Restricted to <see cref="AuthorizationComposition.CnasAdmin"/>.
/// </summary>
/// <param name="service">Maintenance-window service façade.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/maintenance-windows")]
public sealed class MaintenanceWindowsController(IMaintenanceWindowService service) : ControllerBase
{
    private readonly IMaintenanceWindowService _service = service;

    /// <summary>Creates a new maintenance window.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<MaintenanceWindowDto>> CreateAsync(
        [FromBody] MaintenanceWindowCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MaintenanceWindowDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Posts the public notice (Draft → NoticePeriod).</summary>
    /// <param name="sqid">Sqid-encoded window id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/post-notice")]
    public async Task<ActionResult<MaintenanceWindowDto>> PostNoticeAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.PostNoticeAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MaintenanceWindowDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Approves the window (NoticePeriod → Approved).</summary>
    /// <param name="sqid">Sqid-encoded window id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/approve")]
    public async Task<ActionResult<MaintenanceWindowDto>> ApproveAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ApproveAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MaintenanceWindowDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Starts the maintenance (Approved → InProgress).</summary>
    /// <param name="sqid">Sqid-encoded window id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/start")]
    public async Task<ActionResult<MaintenanceWindowDto>> StartAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.StartAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MaintenanceWindowDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Completes the maintenance (InProgress → Completed).</summary>
    /// <param name="sqid">Sqid-encoded window id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/complete")]
    public async Task<ActionResult<MaintenanceWindowDto>> CompleteAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.CompleteAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MaintenanceWindowDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Cancels the window.</summary>
    /// <param name="sqid">Sqid-encoded window id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/cancel")]
    [Consumes("application/json")]
    public async Task<ActionResult<MaintenanceWindowDto>> CancelAsync(
        string sqid,
        [FromBody] MaintenanceWindowReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CancelAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MaintenanceWindowDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets a window by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded window id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<MaintenanceWindowDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MaintenanceWindowDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists windows matching the filter.</summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="windowKind">Optional kind filter.</param>
    /// <param name="scheduledStartAfterUtc">Optional lower bound on ScheduledStartUtc.</param>
    /// <param name="scheduledStartBeforeUtc">Optional upper bound on ScheduledStartUtc.</param>
    /// <param name="skip">Page offset.</param>
    /// <param name="take">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the page on success.</returns>
    [HttpGet]
    public async Task<ActionResult<MaintenanceWindowPageDto>> ListAsync(
        [FromQuery] string? status = null,
        [FromQuery] string? windowKind = null,
        [FromQuery] DateTime? scheduledStartAfterUtc = null,
        [FromQuery] DateTime? scheduledStartBeforeUtc = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new MaintenanceWindowFilterDto(status, windowKind, scheduledStartAfterUtc, scheduledStartBeforeUtc, skip, take);
        var result = await _service.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MaintenanceWindowPageDto>(result.ErrorCode!, result.ErrorMessage!);
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
            ErrorCodes.MaintenanceDurationExceeded => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.BusinessHoursPolicyNotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.MaintenanceInvalidTransition => Conflict(new { error = errorCode, message = errorMessage }),
            ErrorCodes.MaintenanceNoticeLeadTimeInsufficient => Conflict(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
