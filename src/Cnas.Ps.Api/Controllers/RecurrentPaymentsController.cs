using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R1000..R1034 / TOR §3.2-Z — admin REST surface over the
/// recurrent-payment scheduler. Restricted to
/// <see cref="AuthorizationComposition.CnasAdmin"/>.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/recurrent-payments")]
public sealed class RecurrentPaymentsController(IRecurrentPaymentSchedulerService service) : ControllerBase
{
    private readonly IRecurrentPaymentSchedulerService _service = service;

    /// <summary>Creates a new recurrent-payment schedule.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the persisted DTO; 400 on validation failure.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<RecurrentPaymentScheduleDto>> CreateAsync(
        [FromBody] RecurrentPaymentScheduleCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<RecurrentPaymentScheduleDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists schedules ordered by NextPaymentDate.</summary>
    /// <param name="skip">Page offset (≥ 0).</param>
    /// <param name="take">Page size (1..100).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the page; 400 on validation failure.</returns>
    [HttpGet]
    public async Task<ActionResult<RecurrentPaymentSchedulePageDto>> ListAsync(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ListAsync(skip, take, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<RecurrentPaymentSchedulePageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Suspends an Active schedule.</summary>
    /// <param name="sqid">Sqid-encoded schedule id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO; 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/suspend")]
    public async Task<ActionResult<RecurrentPaymentScheduleDto>> SuspendAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.SuspendAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<RecurrentPaymentScheduleDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Resumes a suspended schedule.</summary>
    /// <param name="sqid">Sqid-encoded schedule id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO; 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/resume")]
    public async Task<ActionResult<RecurrentPaymentScheduleDto>> ResumeAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ResumeAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<RecurrentPaymentScheduleDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Soft-deletes a schedule.</summary>
    /// <param name="sqid">Sqid-encoded schedule id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 404 on missing row.</returns>
    [HttpDelete("{sqid}")]
    public async Task<ActionResult> DeleteAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.DeleteAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess) return NoContent();
        return MapNonGeneric(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Generic→specific failure mapper.</summary>
    /// <typeparam name="T">DTO type.</typeparam>
    /// <param name="errorCode">Stable error code.</param>
    /// <param name="errorMessage">Description.</param>
    /// <returns>Mapped HTTP status.</returns>
    private ActionResult<T> MapFailure<T>(string errorCode, string errorMessage)
        => errorCode switch
        {
            ErrorCodes.InvalidSqid => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.ValidationFailed => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            IRecurrentPaymentSchedulerService.ScheduleNotFoundCode => NotFound(new { error = errorCode, message = errorMessage }),
            IRecurrentPaymentSchedulerService.InvalidTransitionCode => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };

    /// <summary>Non-generic mapper for endpoints returning <see cref="ActionResult"/> only.</summary>
    /// <param name="errorCode">Stable error code.</param>
    /// <param name="errorMessage">Description.</param>
    /// <returns>Mapped HTTP status.</returns>
    private ActionResult MapNonGeneric(string errorCode, string errorMessage)
        => errorCode switch
        {
            ErrorCodes.InvalidSqid => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.ValidationFailed => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            IRecurrentPaymentSchedulerService.ScheduleNotFoundCode => NotFound(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
