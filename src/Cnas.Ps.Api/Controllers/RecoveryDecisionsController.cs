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
/// R1505 / TOR §3.7-F — REST surface backing the CNAS-initiated
/// "Decizia de recuperare a sumelor" workflow.
/// Endpoints:
/// <list type="bullet">
///   <item><c>POST /api/decisions/recovery</c> — initiate a recovery decision.</item>
///   <item><c>POST /api/decisions/recovery/{sqid}/acknowledge</c> — solicitant acknowledged debt.</item>
///   <item><c>POST /api/decisions/recovery/{sqid}/recovered</c> — record a recovery payment.</item>
/// </list>
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasDecider)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/decisions/recovery")]
public sealed class RecoveryDecisionsController : ControllerBase
{
    private readonly IRecoveryDecisionService _svc;
    private readonly IValidator<RecoveryDecisionInputDto> _validator;
    private readonly IValidator<RecoveryRecordedInputDto> _recoveredValidator;

    /// <summary>Constructs the controller with the underlying service.</summary>
    /// <param name="svc">Recovery-workflow service.</param>
    /// <param name="validator">FluentValidation validator for initiation input.</param>
    /// <param name="recoveredValidator">FluentValidation validator for the recovered-amount input body.</param>
    public RecoveryDecisionsController(
        IRecoveryDecisionService svc,
        IValidator<RecoveryDecisionInputDto> validator,
        IValidator<RecoveryRecordedInputDto> recoveredValidator)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(recoveredValidator);
        _svc = svc;
        _validator = validator;
        _recoveredValidator = recoveredValidator;
    }

    /// <summary>R1505 — initiate a recovery decision against a beneficiary.</summary>
    /// <param name="input">Recovery decision input.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the freshly-minted DTO, or 400/404 on failure.</returns>
    [HttpPost]
    public async Task<ActionResult<RecoveryDecisionDto>> InitiateAsync(
        [FromBody] RecoveryDecisionInputDto input,
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

        var result = await _svc.InitiateAsync(
            input.SolicitantSqid,
            input.Amount,
            input.Reason,
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<RecoveryDecisionDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R1505 — solicitant acknowledged the debt.</summary>
    /// <param name="sqid">Sqid-encoded id of the recovery decision.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 on success, 404/409 on failure.</returns>
    [HttpPost("{sqid}/acknowledge")]
    public async Task<IActionResult> AcknowledgeAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.MarkAcknowledgedAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R1505 — record a recovery payment against the decision.</summary>
    /// <param name="sqid">Sqid-encoded id of the recovery decision.</param>
    /// <param name="input">Body carrying the recovered amount in MDL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 on success, 400/404/409 on failure.</returns>
    [HttpPost("{sqid}/recovered")]
    public async Task<IActionResult> RecordRecoveredAsync(
        [FromRoute] string sqid,
        [FromBody] RecoveryRecordedInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // FluentValidation gate — enforce strictly-positive amount + sanity cap before
        // forwarding to the service. Failures collapse to 400 ProblemDetails so the
        // service never sees a malformed amount.
        var validation = await _recoveredValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _svc.MarkRecoveredAsync(sqid, input.RecoveredAmount, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a generic Result failure code to the matching HTTP status.</summary>
    /// <param name="code">Stable error code from the service.</param>
    /// <param name="message">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private IActionResult MapFailure(string? code, string? message)
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

    /// <summary>Generic-typed wrapper around <see cref="MapFailure(string, string)"/>.</summary>
    /// <typeparam name="T">Action-result payload type.</typeparam>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Human-readable description.</param>
    /// <returns>Typed ActionResult carrying the appropriate ProblemDetails / NotFound.</returns>
    private ActionResult<T> MapFailureGeneric<T>(string? code, string? message)
    {
        var inner = MapFailure(code, message);
        if (inner is NotFoundResult nf)
        {
            return nf;
        }
        if (inner is ActionResult ar)
        {
            return ar;
        }
        return new ObjectResult(message) { StatusCode = StatusCodes.Status500InternalServerError };
    }
}

// RecoveryRecordedInputDto moved to Cnas.Ps.Contracts.RecoveryDecisionDtos.cs so the
// Application-layer FluentValidation validator can reference it from outside the API
// assembly (validators auto-register via AddValidatorsFromAssemblyContaining<...>).
