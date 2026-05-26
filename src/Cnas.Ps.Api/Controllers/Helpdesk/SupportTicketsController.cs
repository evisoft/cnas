using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Helpdesk;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cnas.Ps.Api.Controllers.Helpdesk;

/// <summary>
/// R2500 / TOR PIR 020-023 — REST surface over the helpdesk ticket
/// lifecycle. Any authenticated caller can submit / comment / view their
/// own tickets; operator transitions (acknowledge / assign / resolve /
/// escalate) are policy-gated by the service layer through the caller
/// context. The controller deliberately uses <see cref="AuthorizeAttribute"/>
/// without a policy name so the umbrella authenticated-user check applies;
/// the service-layer "is the caller the requester OR an operator" guard
/// then enforces visibility on read.
/// </summary>
/// <param name="service">Helpdesk ticket service façade.</param>
[ApiController]
[Authorize]
[Route("api/support-tickets")]
public sealed class SupportTicketsController(ISupportTicketService service) : ControllerBase
{
    private readonly ISupportTicketService _service = service;

    /// <summary>Submits a new helpdesk ticket; the caller becomes the requester.</summary>
    /// <param name="input">Submit payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>201 with the created ticket; 400 / 404 / 409 on failure.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<SupportTicketDto>> SubmitAsync(
        [FromBody] SupportTicketSubmitInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.SubmitAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? StatusCode(201, result.Value)
            : MapFailure<SupportTicketDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Acknowledges a Submitted ticket (operator first response).</summary>
    /// <param name="sqid">Sqid-encoded ticket id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated ticket.</returns>
    [HttpPost("{sqid}/acknowledge")]
    public async Task<ActionResult<SupportTicketDto>> AcknowledgeAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.AcknowledgeAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Assigns the ticket to an operator.</summary>
    /// <param name="sqid">Sqid-encoded ticket id.</param>
    /// <param name="input">Assign payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated ticket.</returns>
    [HttpPost("{sqid}/assign")]
    [Consumes("application/json")]
    public async Task<ActionResult<SupportTicketDto>> AssignAsync(
        string sqid,
        [FromBody] SupportTicketAssignInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.AssignAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Transitions Acknowledged → InProgress.</summary>
    /// <param name="sqid">Sqid-encoded ticket id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated ticket.</returns>
    [HttpPost("{sqid}/start-progress")]
    public async Task<ActionResult<SupportTicketDto>> StartProgressAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.StartProgressAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Transitions InProgress → WaitingOnRequester with a comment.</summary>
    /// <param name="sqid">Sqid-encoded ticket id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated ticket.</returns>
    [HttpPost("{sqid}/request-requester-reply")]
    [Consumes("application/json")]
    public async Task<ActionResult<SupportTicketDto>> RequestRequesterReplyAsync(
        string sqid,
        [FromBody] SupportTicketReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.RequestRequesterReplyAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Transitions WaitingOnRequester → InProgress.</summary>
    /// <param name="sqid">Sqid-encoded ticket id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated ticket.</returns>
    [HttpPost("{sqid}/resume-from-requester")]
    public async Task<ActionResult<SupportTicketDto>> ResumeFromRequesterAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ResumeFromRequesterAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Escalates the ticket.</summary>
    /// <param name="sqid">Sqid-encoded ticket id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated ticket.</returns>
    [HttpPost("{sqid}/escalate")]
    [Consumes("application/json")]
    public async Task<ActionResult<SupportTicketDto>> EscalateAsync(
        string sqid,
        [FromBody] SupportTicketReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.EscalateAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Resolves the ticket.</summary>
    /// <param name="sqid">Sqid-encoded ticket id.</param>
    /// <param name="input">Resolution payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated ticket.</returns>
    [HttpPost("{sqid}/resolve")]
    [Consumes("application/json")]
    public async Task<ActionResult<SupportTicketDto>> ResolveAsync(
        string sqid,
        [FromBody] SupportTicketResolutionInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ResolveAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Closes a Resolved ticket.</summary>
    /// <param name="sqid">Sqid-encoded ticket id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated ticket.</returns>
    [HttpPost("{sqid}/close")]
    public async Task<ActionResult<SupportTicketDto>> CloseAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.CloseAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Reopens a Resolved or Closed ticket (within 7 days).</summary>
    /// <param name="sqid">Sqid-encoded ticket id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated ticket.</returns>
    [HttpPost("{sqid}/reopen")]
    public async Task<ActionResult<SupportTicketDto>> ReopenAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ReopenAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Cancels any non-terminal ticket with a reason.</summary>
    /// <param name="sqid">Sqid-encoded ticket id.</param>
    /// <param name="input">Cancellation payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated ticket.</returns>
    [HttpPost("{sqid}/cancel")]
    [Consumes("application/json")]
    public async Task<ActionResult<SupportTicketDto>> CancelAsync(
        string sqid,
        [FromBody] SupportTicketReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CancelAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Adds a comment to the ticket timeline.</summary>
    /// <param name="sqid">Sqid-encoded ticket id.</param>
    /// <param name="input">Comment payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated ticket.</returns>
    [HttpPost("{sqid}/comments")]
    [Consumes("application/json")]
    public async Task<ActionResult<SupportTicketDto>> AddCommentAsync(
        string sqid,
        [FromBody] SupportTicketCommentInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.AddCommentAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets a ticket by Sqid (includes comments + SLA events).</summary>
    /// <param name="sqid">Sqid-encoded ticket id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the ticket; 400 / 404 on failure.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<SupportTicketDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists tickets matching the filter.</summary>
    /// <param name="status">Optional status filter (enum-name).</param>
    /// <param name="categoryCode">Optional category-code filter.</param>
    /// <param name="submittedByUserSqid">Optional requester filter (Sqid).</param>
    /// <param name="assignedToUserSqid">Optional assignee filter (Sqid).</param>
    /// <param name="severity">Optional severity filter (enum-name).</param>
    /// <param name="skip">Page offset (≥ 0).</param>
    /// <param name="take">Page size (1..100).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the page; 400 on validation failure.</returns>
    [HttpGet]
    public async Task<ActionResult<SupportTicketPageDto>> ListAsync(
        [FromQuery] string? status = null,
        [FromQuery] string? categoryCode = null,
        [FromQuery] string? submittedByUserSqid = null,
        [FromQuery] string? assignedToUserSqid = null,
        [FromQuery] string? severity = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new SupportTicketFilterDto(
            status, categoryCode, submittedByUserSqid, assignedToUserSqid, severity, skip, take);
        var result = await _service.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SupportTicketPageDto>(result.ErrorCode!, result.ErrorMessage!);
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
            ErrorCodes.Unauthorized => Unauthorized(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            ISupportTicketService.InvalidTransitionCode => Conflict(new { error = errorCode, message = errorMessage }),
            ISupportTicketService.ReopenWindowExpiredCode => Conflict(new { error = errorCode, message = errorMessage }),
            ISupportTicketService.CallerNotAuthorisedCode => StatusCode(403, new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
