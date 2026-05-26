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
/// R2503 / TOR PIR 022-023 — admin REST surface over the system-update
/// schedule registry. Restricted to
/// <see cref="AuthorizationComposition.CnasAdmin"/>.
/// </summary>
/// <param name="service">System-update schedule service façade.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/system-updates/schedules")]
public sealed class SystemUpdateSchedulesController(ISystemUpdateScheduleService service) : ControllerBase
{
    private readonly ISystemUpdateScheduleService _service = service;

    /// <summary>Creates a new schedule.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<SystemUpdateScheduleDto>> CreateAsync(
        [FromBody] SystemUpdateScheduleCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SystemUpdateScheduleDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Modifies an existing schedule.</summary>
    /// <param name="sqid">Sqid-encoded schedule id.</param>
    /// <param name="input">Modify payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPut("{sqid}")]
    [Consumes("application/json")]
    public async Task<ActionResult<SystemUpdateScheduleDto>> ModifyAsync(
        string sqid,
        [FromBody] SystemUpdateScheduleModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ModifyAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SystemUpdateScheduleDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Activates a schedule.</summary>
    /// <param name="sqid">Sqid-encoded schedule id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/activate")]
    public async Task<ActionResult<SystemUpdateScheduleDto>> ActivateAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ActivateAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SystemUpdateScheduleDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Deactivates a schedule.</summary>
    /// <param name="sqid">Sqid-encoded schedule id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/deactivate")]
    public async Task<ActionResult<SystemUpdateScheduleDto>> DeactivateAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.DeactivateAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SystemUpdateScheduleDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets a schedule by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded schedule id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<SystemUpdateScheduleDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SystemUpdateScheduleDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets a schedule by stable code.</summary>
    /// <param name="code">Stable schedule code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpGet("by-code/{code}")]
    public async Task<ActionResult<SystemUpdateScheduleDto>> GetByCodeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByCodeAsync(code, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SystemUpdateScheduleDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists schedules.</summary>
    /// <param name="isActive">Optional IsActive filter.</param>
    /// <param name="cadence">Optional cadence filter.</param>
    /// <param name="skip">Page offset.</param>
    /// <param name="take">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the page on success.</returns>
    [HttpGet]
    public async Task<ActionResult<SystemUpdateSchedulePageDto>> ListAsync(
        [FromQuery] bool? isActive = null,
        [FromQuery] string? cadence = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new SystemUpdateScheduleFilterDto(isActive, cadence, skip, take);
        var result = await _service.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SystemUpdateSchedulePageDto>(result.ErrorCode!, result.ErrorMessage!);
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
            ErrorCodes.UpdateScheduleDuplicateCode => Conflict(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
