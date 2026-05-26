using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R1504 / TOR §3.7-E — REST surface backing the CNAS-initiated payment
/// suspend / resume workflow. Endpoints:
/// <list type="bullet">
///   <item><c>POST /api/decisions/{sqid}/suspend-payment</c> — suspend payments against a prior decision.</item>
///   <item><c>POST /api/payment-suspensions/{sqid}/resume</c> — resume suspended payments.</item>
/// </list>
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasDecider)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
public sealed class PaymentSuspensionsController : ControllerBase
{
    private readonly IPaymentSuspensionService _svc;
    private readonly IValidator<PaymentSuspensionInputDto> _validator;

    /// <summary>Constructs the controller.</summary>
    /// <param name="svc">Suspend/resume service.</param>
    /// <param name="validator">FluentValidation validator.</param>
    public PaymentSuspensionsController(
        IPaymentSuspensionService svc,
        IValidator<PaymentSuspensionInputDto> validator)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(validator);
        _svc = svc;
        _validator = validator;
    }

    /// <summary>R1504 — suspend payments against a prior decision.</summary>
    /// <param name="sqid">Sqid-encoded id of the prior decision.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the freshly-minted suspension DTO, 400/404/409 on failure.</returns>
    [HttpPost("api/decisions/{sqid}/suspend-payment")]
    public async Task<ActionResult<PaymentSuspensionDto>> SuspendAsync(
        [FromRoute] string sqid,
        [FromBody] PaymentSuspensionInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _validator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _svc.SuspendAsync(sqid, input.Reason, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R1504 — resume payments against a prior suspension.</summary>
    /// <param name="sqid">Sqid-encoded id of the suspension record.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated suspension DTO, 400/404/409 on failure.</returns>
    [HttpPost("api/payment-suspensions/{sqid}/resume")]
    public async Task<ActionResult<PaymentSuspensionDto>> ResumeAsync(
        [FromRoute] string sqid,
        [FromBody] PaymentSuspensionInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _validator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _svc.ResumeAsync(sqid, input.Reason, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps service failures to typed action results.</summary>
    private ActionResult<PaymentSuspensionDto> MapFailure(string? code, string? message)
    {
        var status = code switch
        {
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            ErrorCodes.Conflict => StatusCodes.Status409Conflict,
            ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest,
        };
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }
}
