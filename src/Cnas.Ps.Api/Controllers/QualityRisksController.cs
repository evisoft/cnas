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
/// R2506 / TOR PIR 037-040 — admin REST surface over the QA-risk registry.
/// Restricted to <see cref="AuthorizationComposition.CnasAdmin"/>.
/// </summary>
/// <param name="service">Quality-risk service façade.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/quality-risks")]
public sealed class QualityRisksController(IQualityRiskService service) : ControllerBase
{
    private readonly IQualityRiskService _service = service;

    /// <summary>Creates a new quality risk.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<QualityRiskDto>> CreateAsync(
        [FromBody] QualityRiskCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CreateRiskAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<QualityRiskDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Modifies an existing risk.</summary>
    /// <param name="sqid">Sqid-encoded risk id.</param>
    /// <param name="input">Modify payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/modify")]
    [Consumes("application/json")]
    public async Task<ActionResult<QualityRiskDto>> ModifyAsync(
        string sqid,
        [FromBody] QualityRiskModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ModifyRiskAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<QualityRiskDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Closes a risk.</summary>
    /// <param name="sqid">Sqid-encoded risk id.</param>
    /// <param name="input">Closure-reason payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/close")]
    [Consumes("application/json")]
    public async Task<ActionResult<QualityRiskDto>> CloseAsync(
        string sqid,
        [FromBody] QualityRiskReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CloseRiskAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<QualityRiskDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Marks a risk Mitigating.</summary>
    /// <param name="sqid">Sqid-encoded risk id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/mark-mitigating")]
    public async Task<ActionResult<QualityRiskDto>> MarkMitigatingAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.MarkMitigatingAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<QualityRiskDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Formally accepts a risk.</summary>
    /// <param name="sqid">Sqid-encoded risk id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/accept")]
    [Consumes("application/json")]
    public async Task<ActionResult<QualityRiskDto>> AcceptAsync(
        string sqid,
        [FromBody] QualityRiskReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.AcceptRiskAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<QualityRiskDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Records a periodic review.</summary>
    /// <param name="sqid">Sqid-encoded risk id.</param>
    /// <param name="input">Review payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/record-review")]
    [Consumes("application/json")]
    public async Task<ActionResult<QualityRiskDto>> RecordReviewAsync(
        string sqid,
        [FromBody] QualityRiskReviewInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.RecordReviewAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<QualityRiskDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Adds a preventive action to a risk.</summary>
    /// <param name="sqid">Sqid-encoded risk id.</param>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/actions")]
    [Consumes("application/json")]
    public async Task<ActionResult<QualityRiskActionDto>> AddActionAsync(
        string sqid,
        [FromBody] QualityRiskActionCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.AddPreventiveActionAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<QualityRiskActionDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Modifies a preventive action.</summary>
    /// <param name="actionSqid">Sqid-encoded action id.</param>
    /// <param name="input">Modify payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("actions/{actionSqid}/modify")]
    [Consumes("application/json")]
    public async Task<ActionResult<QualityRiskActionDto>> ModifyActionAsync(
        string actionSqid,
        [FromBody] QualityRiskActionModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ModifyPreventiveActionAsync(actionSqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<QualityRiskActionDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Marks an action InProgress.</summary>
    /// <param name="actionSqid">Sqid-encoded action id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("actions/{actionSqid}/mark-in-progress")]
    public async Task<ActionResult<QualityRiskActionDto>> MarkActionInProgressAsync(
        string actionSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.MarkActionInProgressAsync(actionSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<QualityRiskActionDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Marks an action Implemented.</summary>
    /// <param name="actionSqid">Sqid-encoded action id.</param>
    /// <param name="input">Completion-note payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("actions/{actionSqid}/mark-implemented")]
    [Consumes("application/json")]
    public async Task<ActionResult<QualityRiskActionDto>> MarkActionImplementedAsync(
        string actionSqid,
        [FromBody] QualityRiskActionImplementInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.MarkActionImplementedAsync(actionSqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<QualityRiskActionDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Cancels a preventive action.</summary>
    /// <param name="actionSqid">Sqid-encoded action id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("actions/{actionSqid}/cancel")]
    [Consumes("application/json")]
    public async Task<ActionResult<QualityRiskActionDto>> CancelActionAsync(
        string actionSqid,
        [FromBody] QualityRiskActionReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CancelActionAsync(actionSqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<QualityRiskActionDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets a risk by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded risk id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<QualityRiskDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetRiskByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<QualityRiskDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists risks matching the filter.</summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="category">Optional category filter.</param>
    /// <param name="likelihood">Optional likelihood filter.</param>
    /// <param name="impact">Optional impact filter.</param>
    /// <param name="ownerSqid">Optional owner filter.</param>
    /// <param name="overdueForReview">Optional overdue-review filter.</param>
    /// <param name="skip">Page offset.</param>
    /// <param name="take">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the page on success.</returns>
    [HttpGet]
    public async Task<ActionResult<QualityRiskPageDto>> ListAsync(
        [FromQuery] string? status = null,
        [FromQuery] string? category = null,
        [FromQuery] string? likelihood = null,
        [FromQuery] string? impact = null,
        [FromQuery] string? ownerSqid = null,
        [FromQuery] bool? overdueForReview = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new QualityRiskFilterDto(status, category, likelihood, impact, ownerSqid, overdueForReview, skip, take);
        var result = await _service.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<QualityRiskPageDto>(result.ErrorCode!, result.ErrorMessage!);
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
            ErrorCodes.Unauthorized => Unauthorized(new { error = errorCode, message = errorMessage }),
            ErrorCodes.QualityRiskNotOwner => StatusCode(403, new { error = errorCode, message = errorMessage }),
            ErrorCodes.QualityRiskInvalidTransition => Conflict(new { error = errorCode, message = errorMessage }),
            ErrorCodes.QualityRiskDuplicateCode => Conflict(new { error = errorCode, message = errorMessage }),
            ErrorCodes.QualityRiskActionInvalidTransition => Conflict(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
