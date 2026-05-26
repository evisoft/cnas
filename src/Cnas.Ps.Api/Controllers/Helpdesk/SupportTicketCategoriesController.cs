using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Helpdesk;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers.Helpdesk;

/// <summary>
/// R2500 / TOR PIR 020-023 — admin REST surface over the helpdesk-category
/// registry. Restricted to <see cref="AuthorizationComposition.CnasAdmin"/>.
/// </summary>
/// <param name="service">Helpdesk-category service façade.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/helpdesk/categories")]
public sealed class SupportTicketCategoriesController(ISupportTicketCategoryService service) : ControllerBase
{
    private readonly ISupportTicketCategoryService _service = service;

    /// <summary>Creates a new helpdesk category.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the created category; 400 / 409 on failure.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<SupportTicketCategoryDto>> CreateAsync(
        [FromBody] SupportTicketCategoryCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketCategoryDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Modifies an existing category.</summary>
    /// <param name="sqid">Sqid-encoded category id.</param>
    /// <param name="input">Modify payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated category; 400 / 404 on failure.</returns>
    [HttpPut("{sqid}")]
    [Consumes("application/json")]
    public async Task<ActionResult<SupportTicketCategoryDto>> ModifyAsync(
        string sqid,
        [FromBody] SupportTicketCategoryModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ModifyAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketCategoryDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Activates an Inactive category.</summary>
    /// <param name="sqid">Sqid-encoded category id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated category.</returns>
    [HttpPost("{sqid}/activate")]
    public async Task<ActionResult<SupportTicketCategoryDto>> ActivateAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ActivateAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketCategoryDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Deactivates an Active category.</summary>
    /// <param name="sqid">Sqid-encoded category id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated category.</returns>
    [HttpPost("{sqid}/deactivate")]
    public async Task<ActionResult<SupportTicketCategoryDto>> DeactivateAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.DeactivateAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketCategoryDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets a category by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded category id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the category; 400 / 404 on failure.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<SupportTicketCategoryDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketCategoryDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets a category by its stable code.</summary>
    /// <param name="code">Category code (SCREAMING_SNAKE_CASE).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the category; 400 / 404 on failure.</returns>
    [HttpGet("by-code/{code}")]
    public async Task<ActionResult<SupportTicketCategoryDto>> GetByCodeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByCodeAsync(code, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketCategoryDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists categories matching the filter.</summary>
    /// <param name="isActive">Optional IsActive filter.</param>
    /// <param name="skip">Page offset (≥ 0).</param>
    /// <param name="take">Page size (1..100).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the page; 400 on validation failure.</returns>
    [HttpGet]
    public async Task<ActionResult<SupportTicketCategoryPageDto>> ListAsync(
        [FromQuery] bool? isActive = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new SupportTicketCategoryFilterDto(isActive, skip, take);
        var result = await _service.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketCategoryPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Translates a failed <see cref="Result{T}"/> to the appropriate HTTP status.</summary>
    /// <typeparam name="T">DTO type that would have been returned on success.</typeparam>
    /// <param name="errorCode">Stable error code.</param>
    /// <param name="errorMessage">Human-readable description.</param>
    /// <returns>An <see cref="ActionResult{T}"/> carrying the appropriate HTTP status.</returns>
    private ActionResult<T> MapFailure<T>(string errorCode, string errorMessage)
        => errorCode switch
        {
            ErrorCodes.InvalidSqid => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.ValidationFailed => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            ISupportTicketCategoryService.DuplicateCategoryCodeCode => Conflict(new { error = errorCode, message = errorMessage }),
            ISupportTicketCategoryService.InvalidTransitionCode => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
