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
/// R2501 / TOR PIR 024 — admin REST surface over the business-hours policy
/// registry. Restricted to <see cref="AuthorizationComposition.CnasAdmin"/>.
/// </summary>
/// <param name="service">Business-hours policy service façade.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/business-hours/policies")]
public sealed class BusinessHoursPoliciesController(IBusinessHoursPolicyService service) : ControllerBase
{
    private readonly IBusinessHoursPolicyService _service = service;

    /// <summary>Creates a new business-hours policy.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the created policy on success; 400/409 otherwise.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<BusinessHoursPolicyDto>> CreateAsync(
        [FromBody] BusinessHoursPolicyCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BusinessHoursPolicyDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Modifies an existing policy.</summary>
    /// <param name="sqid">Sqid-encoded policy id.</param>
    /// <param name="input">Modify payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated policy on success.</returns>
    [HttpPut("{sqid}")]
    [Consumes("application/json")]
    public async Task<ActionResult<BusinessHoursPolicyDto>> ModifyAsync(
        string sqid,
        [FromBody] BusinessHoursPolicyModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ModifyAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BusinessHoursPolicyDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Activates a policy.</summary>
    /// <param name="sqid">Sqid-encoded policy id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/activate")]
    public async Task<ActionResult<BusinessHoursPolicyDto>> ActivateAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ActivateAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BusinessHoursPolicyDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Deactivates a policy.</summary>
    /// <param name="sqid">Sqid-encoded policy id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/deactivate")]
    public async Task<ActionResult<BusinessHoursPolicyDto>> DeactivateAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.DeactivateAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BusinessHoursPolicyDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets a policy by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded policy id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success; 400 / 404 on failure.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<BusinessHoursPolicyDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BusinessHoursPolicyDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets a policy by stable code.</summary>
    /// <param name="code">Stable policy code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpGet("by-code/{code}")]
    public async Task<ActionResult<BusinessHoursPolicyDto>> GetByCodeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByCodeAsync(code, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BusinessHoursPolicyDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists policies.</summary>
    /// <param name="isActive">Optional IsActive filter.</param>
    /// <param name="skip">Page offset.</param>
    /// <param name="take">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the page on success.</returns>
    [HttpGet]
    public async Task<ActionResult<BusinessHoursPolicyPageDto>> ListAsync(
        [FromQuery] bool? isActive = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new BusinessHoursPolicyFilterDto(isActive, skip, take);
        var result = await _service.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BusinessHoursPolicyPageDto>(result.ErrorCode!, result.ErrorMessage!);
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
            ErrorCodes.BusinessHoursPolicyNotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.BusinessHoursPolicyDuplicateCode => Conflict(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
