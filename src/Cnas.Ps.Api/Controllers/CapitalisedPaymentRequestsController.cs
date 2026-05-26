using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.CapitalisedPayments;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R1202 / TOR §3.4-C — REST surface over the capitalised-payment registry.
/// Restricted to the <see cref="AuthorizationComposition.CnasAdmin"/> policy
/// because capitalised-payment lifecycle transitions touch sensitive financial
/// data (creditor sums, beneficiary IDNP).
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST /api/capitalised-payments</c> — create a request in <c>Draft</c>.</item>
///   <item><c>PUT  /api/capitalised-payments/{sqid}</c> — modify a <c>Draft</c> request.</item>
///   <item><c>POST /api/capitalised-payments/{sqid}/submit</c> — transition <c>Draft</c> → <c>Submitted</c>.</item>
///   <item><c>POST /api/capitalised-payments/{sqid}/compute</c> — run the present-value computation.</item>
///   <item><c>POST /api/capitalised-payments/{sqid}/approve</c> — approve a computed decision.</item>
///   <item><c>POST /api/capitalised-payments/{sqid}/reject</c> — reject a computed decision.</item>
///   <item><c>POST /api/capitalised-payments/{sqid}/settle</c> — record liquidator-paid treasury receipt.</item>
///   <item><c>POST /api/capitalised-payments/{sqid}/cancel</c> — cancel a non-terminal request.</item>
///   <item><c>GET  /api/capitalised-payments/{sqid}</c> — fetch a single request.</item>
///   <item><c>GET  /api/capitalised-payments/{sqid}/latest-decision</c> — fetch the most recent decision.</item>
///   <item><c>GET  /api/capitalised-payments?status=…&amp;obligationKind=…&amp;skip=…&amp;take=…</c> — list requests.</item>
/// </list>
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/capitalised-payments")]
public sealed class CapitalisedPaymentRequestsController : ControllerBase
{
    private readonly ICapitalisedPaymentService _service;

    /// <summary>Constructs the controller with its scoped collaborator.</summary>
    /// <param name="service">Capitalised-payment service façade.</param>
    public CapitalisedPaymentRequestsController(ICapitalisedPaymentService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <summary>POST <c>/api/capitalised-payments</c> — create a request in <c>Draft</c>.</summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>201 with the created DTO; 400 / 401 / 409 on failure.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<CapitalisedPaymentRequestDto>> CreateAsync(
        [FromBody] CapitalisedPaymentRequestCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Created($"/api/capitalised-payments/{result.Value.Id}", result.Value)
            : MapFailure<CapitalisedPaymentRequestDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>PUT <c>/api/capitalised-payments/{sqid}</c> — modify a <c>Draft</c> request.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="input">Validated modify envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated DTO; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPut("{sqid}")]
    [Consumes("application/json")]
    public async Task<ActionResult<CapitalisedPaymentRequestDto>> ModifyAsync(
        string sqid,
        [FromBody] CapitalisedPaymentRequestModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ModifyAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<CapitalisedPaymentRequestDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/capitalised-payments/{sqid}/submit</c> — transition <c>Draft</c> → <c>Submitted</c>.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated DTO; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/submit")]
    public async Task<ActionResult<CapitalisedPaymentRequestDto>> SubmitAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.SubmitAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<CapitalisedPaymentRequestDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/capitalised-payments/{sqid}/compute</c> — run the present-value computation.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the persisted decision DTO; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/compute")]
    public async Task<ActionResult<CapitalisedPaymentDecisionDto>> ComputeAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ComputeAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<CapitalisedPaymentDecisionDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/capitalised-payments/{sqid}/approve</c> — approve a computed decision.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="input">Approval envelope (mandatory note).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated decision DTO; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/approve")]
    [Consumes("application/json")]
    public async Task<ActionResult<CapitalisedPaymentDecisionDto>> ApproveAsync(
        string sqid,
        [FromBody] CapitalisedPaymentApprovalInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ApproveAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<CapitalisedPaymentDecisionDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/capitalised-payments/{sqid}/reject</c> — reject a computed decision.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated decision DTO; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/reject")]
    [Consumes("application/json")]
    public async Task<ActionResult<CapitalisedPaymentDecisionDto>> RejectAsync(
        string sqid,
        [FromBody] CapitalisedPaymentReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.RejectAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<CapitalisedPaymentDecisionDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/capitalised-payments/{sqid}/settle</c> — record liquidator-paid treasury receipt.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="input">Settlement envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated DTO; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/settle")]
    [Consumes("application/json")]
    public async Task<ActionResult<CapitalisedPaymentRequestDto>> SettleAsync(
        string sqid,
        [FromBody] CapitalisedPaymentSettlementInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.MarkSettledAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<CapitalisedPaymentRequestDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/capitalised-payments/{sqid}/cancel</c> — cancel a non-terminal request.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated DTO; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/cancel")]
    [Consumes("application/json")]
    public async Task<ActionResult<CapitalisedPaymentRequestDto>> CancelAsync(
        string sqid,
        [FromBody] CapitalisedPaymentReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CancelAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<CapitalisedPaymentRequestDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>GET <c>/api/capitalised-payments/{sqid}</c> — fetch a single request.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the DTO; 400 / 401 / 404 on failure.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<CapitalisedPaymentRequestDto>> GetAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<CapitalisedPaymentRequestDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>GET <c>/api/capitalised-payments/{sqid}/latest-decision</c> — fetch the most recent decision.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the decision DTO; 400 / 401 / 404 on failure.</returns>
    [HttpGet("{sqid}/latest-decision")]
    public async Task<ActionResult<CapitalisedPaymentDecisionDto>> GetLatestDecisionAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetLatestDecisionAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<CapitalisedPaymentDecisionDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>GET <c>/api/capitalised-payments</c> — list requests with filters.</summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="obligationKind">Optional obligation-kind filter.</param>
    /// <param name="beneficiaryIdnpHash">Optional beneficiary IDNP hash filter.</param>
    /// <param name="skip">Page offset (≥ 0).</param>
    /// <param name="take">Page size (1..100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the page DTO; 400 on validation failure.</returns>
    [HttpGet]
    public async Task<ActionResult<CapitalisedPaymentRequestPageDto>> ListAsync(
        [FromQuery] string? status = null,
        [FromQuery] string? obligationKind = null,
        [FromQuery] string? beneficiaryIdnpHash = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 25,
        CancellationToken cancellationToken = default)
    {
        var filter = new CapitalisedPaymentRequestFilterDto(
            Status: status,
            ObligationKind: obligationKind,
            BeneficiaryIdnpHash: beneficiaryIdnpHash,
            Skip: skip,
            Take: take);
        var result = await _service.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<CapitalisedPaymentRequestPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Translates a failed <see cref="Result{T}"/> into the appropriate
    /// <see cref="ActionResult"/>.
    /// </summary>
    /// <typeparam name="T">DTO type that would have been returned on success.</typeparam>
    /// <param name="errorCode">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="errorMessage">Human-readable description.</param>
    /// <returns>An <see cref="ActionResult{T}"/> carrying the appropriate HTTP status.</returns>
    private ActionResult<T> MapFailure<T>(string errorCode, string errorMessage)
        => errorCode switch
        {
            ErrorCodes.InvalidSqid => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.ValidationFailed => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Unauthorized => Unauthorized(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
