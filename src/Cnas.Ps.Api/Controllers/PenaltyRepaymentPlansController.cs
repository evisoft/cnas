using Cnas.Ps.Application.Financials;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0817 / TOR BP 1.2-H — REST surface for the staggered-penalty-repayment
/// workflow.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation.</b> Every endpoint is gated by the
/// <c>cnas-admin</c> role — repayment-plan creation, payment registration
/// and cancellation are financial operations restricted to administrative
/// staff.
/// </para>
/// <para>
/// <b>Sqid round-trip.</b> Route parameters are decoded via
/// <see cref="ISqidService.TryDecode"/> before reaching the service layer;
/// outbound DTOs carry Sqid-encoded ids per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "cnas-admin")]
public sealed class PenaltyRepaymentPlansController : ControllerBase
{
    private readonly IPenaltyRepaymentService _svc;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the controller with its collaborators.</summary>
    /// <param name="svc">Penalty-repayment service façade.</param>
    /// <param name="sqids">Sqid encoder/decoder for route parameters.</param>
    public PenaltyRepaymentPlansController(
        IPenaltyRepaymentService svc,
        ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(sqids);
        _svc = svc;
        _sqids = sqids;
    }

    /// <summary>R0817 — create a new staggered-repayment plan for a late-payment penalty.</summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>201 with the persisted DTO; 400/404/409 on failure.</returns>
    [HttpPost("api/penalty-repayment-plans")]
    public async Task<ActionResult<PenaltyRepaymentPlanDto>> CreateAsync(
        [FromBody] PenaltyRepaymentCreatePlanInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.CreatePlanAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : MapFailureGeneric<PenaltyRepaymentPlanDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0817 — register a payment against a specific installment of an Active plan.</summary>
    /// <param name="sqid">Sqid-encoded id of the parent plan.</param>
    /// <param name="installmentNumber">1-based ordinal installment position.</param>
    /// <param name="input">Payment-detail payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the persisted installment DTO; 400/404/409 on failure.</returns>
    [HttpPost("api/penalty-repayment-plans/{sqid}/installments/{installmentNumber:int}/pay")]
    public async Task<ActionResult<PenaltyRepaymentInstallmentDto>> RegisterPaymentAsync(
        string sqid,
        int installmentNumber,
        [FromBody] PenaltyRepaymentRegisterPaymentInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<PenaltyRepaymentInstallmentDto>(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.RegisterInstallmentPaymentByNumberAsync(
            decoded.Value, installmentNumber, input.PaidDate, input.PaidAmount, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<PenaltyRepaymentInstallmentDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0817 — administratively cancel an Active plan.</summary>
    /// <param name="sqid">Sqid-encoded plan id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>204 on success; 400/404/409 on failure.</returns>
    [HttpPost("api/penalty-repayment-plans/{sqid}/cancel")]
    public async Task<IActionResult> CancelAsync(
        string sqid,
        [FromBody] PenaltyRepaymentCancelPlanInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureUntyped(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var result = await _svc.CancelPlanAsync(decoded.Value, input.Reason, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? NoContent()
            : MapFailureUntyped(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0817 — fetch a single plan row by surrogate id.</summary>
    /// <param name="sqid">Sqid-encoded plan id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the DTO when found; 404 otherwise.</returns>
    [HttpGet("api/penalty-repayment-plans/{sqid}")]
    public async Task<ActionResult<PenaltyRepaymentPlanDto>> GetAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<PenaltyRepaymentPlanDto>(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var dto = await _svc.GetAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Maps generic-result failures to ProblemDetails.</summary>
    /// <typeparam name="T">DTO type the action would have returned.</typeparam>
    /// <param name="code">Stable error code from the service.</param>
    /// <param name="message">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ActionResult<T> MapFailureGeneric<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Maps non-generic-result failures to ProblemDetails (untyped action).</summary>
    /// <param name="code">Stable error code from the service.</param>
    /// <param name="message">Human-readable description.</param>
    /// <returns>ProblemDetails IActionResult.</returns>
    private IActionResult MapFailureUntyped(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>404 / 409 / 403 / 400 as appropriate.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Conflict => StatusCodes.Status409Conflict,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status400BadRequest,
    };
}
