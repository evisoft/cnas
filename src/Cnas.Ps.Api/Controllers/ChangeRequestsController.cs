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
/// R2505 / TOR PIR 030-033 — admin REST surface over the change-management
/// aggregate. Restricted to
/// <see cref="AuthorizationComposition.CnasAdmin"/>.
/// </summary>
/// <param name="service">Change-request service façade.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/change-requests")]
public sealed class ChangeRequestsController(IChangeRequestService service) : ControllerBase
{
    private readonly IChangeRequestService _service = service;

    /// <summary>Creates a new change request in Draft.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<ChangeRequestDto>> CreateAsync(
        [FromBody] ChangeRequestCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ChangeRequestDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Submits a Draft change request (Draft → Submitted).</summary>
    /// <param name="sqid">Sqid-encoded change id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/submit")]
    public async Task<ActionResult<ChangeRequestDto>> SubmitAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.SubmitAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ChangeRequestDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Starts technical review (Submitted → InReview).</summary>
    /// <param name="sqid">Sqid-encoded change id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/start-review")]
    public async Task<ActionResult<ChangeRequestDto>> StartReviewAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.StartReviewAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ChangeRequestDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Records the test-env validation (InReview → TestEnvValidated).</summary>
    /// <param name="sqid">Sqid-encoded change id.</param>
    /// <param name="input">Validation-note payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/validate-test-env")]
    [Consumes("application/json")]
    public async Task<ActionResult<ChangeRequestDto>> ValidateTestEnvAsync(
        string sqid,
        [FromBody] ChangeRequestTestValidationInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ValidateTestEnvAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ChangeRequestDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Records the code signature (TestEnvValidated → CodeSigned).</summary>
    /// <param name="sqid">Sqid-encoded change id.</param>
    /// <param name="input">Code-signature payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/sign-code")]
    [Consumes("application/json")]
    public async Task<ActionResult<ChangeRequestDto>> SignCodeAsync(
        string sqid,
        [FromBody] ChangeRequestSignCodeInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.SignCodeAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ChangeRequestDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Approves the change for production (CodeSigned → ApprovedForProd).</summary>
    /// <param name="sqid">Sqid-encoded change id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/approve")]
    public async Task<ActionResult<ChangeRequestDto>> ApproveAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ApproveAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ChangeRequestDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Starts the production deployment.</summary>
    /// <param name="sqid">Sqid-encoded change id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/start-deploy")]
    public async Task<ActionResult<ChangeRequestDto>> StartDeployAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.StartDeploymentAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ChangeRequestDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Completes the production deployment.</summary>
    /// <param name="sqid">Sqid-encoded change id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/complete-deploy")]
    public async Task<ActionResult<ChangeRequestDto>> CompleteDeployAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.CompleteDeploymentAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ChangeRequestDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Rolls back the change.</summary>
    /// <param name="sqid">Sqid-encoded change id.</param>
    /// <param name="input">Rollback-reason payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/rollback")]
    [Consumes("application/json")]
    public async Task<ActionResult<ChangeRequestDto>> RollbackAsync(
        string sqid,
        [FromBody] ChangeRequestRollbackInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.RollBackAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ChangeRequestDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Cancels the change.</summary>
    /// <param name="sqid">Sqid-encoded change id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpPost("{sqid}/cancel")]
    [Consumes("application/json")]
    public async Task<ActionResult<ChangeRequestDto>> CancelAsync(
        string sqid,
        [FromBody] ChangeRequestReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CancelAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ChangeRequestDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets a change request by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded change id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<ChangeRequestDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ChangeRequestDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists change requests matching the filter.</summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="kind">Optional kind filter.</param>
    /// <param name="risk">Optional risk filter.</param>
    /// <param name="skip">Page offset.</param>
    /// <param name="take">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the page on success.</returns>
    [HttpGet]
    public async Task<ActionResult<ChangeRequestPageDto>> ListAsync(
        [FromQuery] string? status = null,
        [FromQuery] string? kind = null,
        [FromQuery] string? risk = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new ChangeRequestFilterDto(status, kind, risk, skip, take);
        var result = await _service.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ChangeRequestPageDto>(result.ErrorCode!, result.ErrorMessage!);
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
            ErrorCodes.ChangeRequestInvalidTransition => Conflict(new { error = errorCode, message = errorMessage }),
            ErrorCodes.ChangeRequestSameOperator => Conflict(new { error = errorCode, message = errorMessage }),
            ErrorCodes.ChangeRequestDuplicateNumber => Conflict(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
