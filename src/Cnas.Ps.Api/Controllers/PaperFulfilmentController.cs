using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Documents;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0602 / TOR CF 11.03 — REST surface for the paper-channel fulfilment
/// workflow. Two namespaces:
/// <c>/api/documents/{sqid}/paper-fulfilment/enqueue</c> for the per-document
/// enqueue, and <c>/api/paper-fulfilment/{sqid}/*</c> for the state
/// transitions. Gated by the <c>cnas-user</c> policy.
/// </summary>
/// <param name="svc">Underlying paper-fulfilment service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasUser)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
public sealed class PaperFulfilmentController(IPaperFulfilmentService svc) : ControllerBase
{
    private readonly IPaperFulfilmentService _svc = svc;

    /// <summary>Enqueues a paper fulfilment for the supplied Document.</summary>
    /// <param name="sqid">Sqid-encoded id of the Document.</param>
    /// <param name="body">Enqueue payload — carries the territorial subdivision.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the persisted DTO; ProblemDetails on failure.</returns>
    [HttpPost("api/documents/{sqid}/paper-fulfilment/enqueue")]
    public async Task<ActionResult<PaperFulfilmentDto>> EnqueueAsync(
        string sqid,
        [FromBody] PaperFulfilmentEnqueueInput body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await _svc.EnqueueAsync(sqid, body.TerritorialSubdivisionCode, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<PaperFulfilmentDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Transitions the supplied fulfilment row to Printed.</summary>
    /// <param name="sqid">Sqid-encoded id of the fulfilment record.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; ProblemDetails on failure.</returns>
    [HttpPost("api/paper-fulfilment/{sqid}/printed")]
    public async Task<IActionResult> MarkPrintedAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.MarkPrintedAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Transitions the supplied fulfilment row to Dispatched.</summary>
    /// <param name="sqid">Sqid-encoded id of the fulfilment record.</param>
    /// <param name="body">Dispatch payload — carries the carrier tracking number.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; ProblemDetails on failure.</returns>
    [HttpPost("api/paper-fulfilment/{sqid}/dispatched")]
    public async Task<IActionResult> MarkDispatchedAsync(
        string sqid,
        [FromBody] PaperFulfilmentDispatchInput body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await _svc.MarkDispatchedAsync(sqid, body.CarrierTrackingNumber, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Transitions the supplied fulfilment row to Delivered.</summary>
    /// <param name="sqid">Sqid-encoded id of the fulfilment record.</param>
    /// <param name="body">Delivery payload — carries the calendar delivery date.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; ProblemDetails on failure.</returns>
    [HttpPost("api/paper-fulfilment/{sqid}/delivered")]
    public async Task<IActionResult> MarkDeliveredAsync(
        string sqid,
        [FromBody] PaperFulfilmentDeliveryInput body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await _svc.MarkDeliveredAsync(sqid, body.DeliveredOn, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a generic <see cref="Result{T}"/> failure.</summary>
    /// <typeparam name="T">DTO type.</typeparam>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Detail.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ActionResult<T> MapFailure<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Maps a non-generic <see cref="Result"/> failure.</summary>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Detail.</param>
    /// <returns>ProblemDetails IActionResult.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates an error code to an HTTP status.</summary>
    /// <param name="code">Error code.</param>
    /// <returns>The HTTP status code.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        ErrorCodes.PaperFulfilmentInvalidTransition => StatusCodes.Status409Conflict,
        ErrorCodes.PaperFulfilmentAlreadyEnqueued => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    };
}
