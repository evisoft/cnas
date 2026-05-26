using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0830 / R0834 / TOR Annex 1 §8.1.4.5 — REST surface for the dedicated
/// insolvency lifecycle registry. All endpoints are gated by the
/// <c>CnasAdmin</c> policy because insolvency events affect the citizen-facing
/// payer registry and downstream claim distributions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST /api/insolvency/open</c>                       — open a new case (201).</item>
///   <item><c>POST /api/insolvency/{sqid}/resolve</c>             — resolve an open case (204).</item>
///   <item><c>GET  /api/insolvency</c>                            — list active cases (200).</item>
///   <item><c>POST /api/insolvency/{caseSqid}/claims</c>          — register a claim (201).</item>
///   <item><c>GET  /api/insolvency/{caseSqid}/claims</c>          — list claims (200).</item>
///   <item><c>POST /api/insolvency/{caseSqid}/payments</c>        — register a payment (201).</item>
///   <item><c>GET  /api/insolvency/{caseSqid}/payments</c>        — list payments (200).</item>
/// </list>
/// </para>
/// <para>
/// <b>Error-code → HTTP status mapping.</b>
/// <see cref="ErrorCodes.NotFound"/> → 404,
/// <see cref="ErrorCodes.Conflict"/> → 409,
/// <see cref="ErrorCodes.Forbidden"/> → 403,
/// <see cref="ErrorCodes.Unauthorized"/> → 401,
/// every other code (<see cref="ErrorCodes.ValidationFailed"/>,
/// <see cref="ErrorCodes.InvalidSqid"/>) → 400.
/// </para>
/// </remarks>
/// <param name="svc">Underlying insolvency-lifecycle service.</param>
/// <param name="resolveValidator">FluentValidation validator for the resolve-input DTO.</param>
/// <param name="claimValidator">FluentValidation validator for the claim-input DTO.</param>
/// <param name="paymentValidator">FluentValidation validator for the payment-input DTO.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/insolvency")]
public sealed class InsolvencyController(
    IInsolvencyLifecycleService svc,
    IValidator<InsolvencyResolveInputDto> resolveValidator,
    IValidator<InsolvencyClaimInputDto> claimValidator,
    IValidator<InsolvencyPaymentInputDto> paymentValidator) : ControllerBase
{
    private readonly IInsolvencyLifecycleService _svc = svc;
    private readonly IValidator<InsolvencyResolveInputDto> _resolveValidator = resolveValidator;
    private readonly IValidator<InsolvencyClaimInputDto> _claimValidator = claimValidator;
    private readonly IValidator<InsolvencyPaymentInputDto> _paymentValidator = paymentValidator;

    /// <summary>Opens a new insolvency lifecycle case for the supplied contributor.</summary>
    /// <param name="body">Request payload carrying the payer + reason + insolvency date.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>201 with the persisted case on success; 400 / 404 / 409 on failure.</returns>
    [HttpPost("open")]
    [Consumes("application/json")]
    public async Task<ActionResult<InsolvencyCaseDto>> OpenAsync(
        [FromBody] InsolvencyOpenInputDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await _svc.OpenAsync(
                body.ContributorSqid,
                body.Reason,
                body.InsolvencyDate,
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Created($"/api/insolvency/{result.Value.Id}", result.Value)
            : MapFailureGeneric<InsolvencyCaseDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Resolves an open insolvency case.</summary>
    /// <param name="sqid">Sqid-encoded id of the case to resolve.</param>
    /// <param name="body">Request payload carrying the resolution rationale.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 400 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/resolve")]
    [Consumes("application/json")]
    public async Task<IActionResult> ResolveAsync(
        [FromRoute] string sqid,
        [FromBody] InsolvencyResolveInputDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        // FluentValidation gate — resolution length must be inside the documented bounds.
        var validation = await _resolveValidator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _svc.ResolveAsync(sqid, body.Resolution, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? NoContent()
            : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Lists every currently-open insolvency case (oldest first).</summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the open cases.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<InsolvencyCaseDto>>> ListActiveAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<IReadOnlyList<InsolvencyCaseDto>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Registers a claim against the supplied insolvency case.</summary>
    /// <param name="caseSqid">Sqid-encoded id of the parent case.</param>
    /// <param name="body">Claim input payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>201 with the persisted claim; 400 / 404 / 409 on failure.</returns>
    [HttpPost("{caseSqid}/claims")]
    [Consumes("application/json")]
    public async Task<ActionResult<InsolvencyClaimDto>> AddClaimAsync(
        [FromRoute] string caseSqid,
        [FromBody] InsolvencyClaimInputDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        // FluentValidation gate — amount, currency, description, and date bounds must be
        // enforced at the controller boundary so the service sees only well-formed input.
        var validation = await _claimValidator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _svc.AddClaimAsync(caseSqid, body, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Created($"/api/insolvency/{caseSqid}/claims/{result.Value.Id}", result.Value)
            : MapFailureGeneric<InsolvencyClaimDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Lists every claim row registered against the supplied case.</summary>
    /// <param name="caseSqid">Sqid-encoded id of the parent case.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the claim rows; 404 when the case is unknown.</returns>
    [HttpGet("{caseSqid}/claims")]
    public async Task<ActionResult<IReadOnlyList<InsolvencyClaimDto>>> ListClaimsAsync(
        [FromRoute] string caseSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ListClaimsAsync(caseSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<IReadOnlyList<InsolvencyClaimDto>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Registers a payment against the supplied insolvency case.</summary>
    /// <param name="caseSqid">Sqid-encoded id of the parent case.</param>
    /// <param name="body">Payment input payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>201 with the persisted payment; 400 / 404 / 409 on failure.</returns>
    [HttpPost("{caseSqid}/payments")]
    [Consumes("application/json")]
    public async Task<ActionResult<InsolvencyPaymentDto>> AddPaymentAsync(
        [FromRoute] string caseSqid,
        [FromBody] InsolvencyPaymentInputDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        // FluentValidation gate — amount, reference, and date bounds must be enforced
        // at the controller boundary so the service sees only well-formed input.
        var validation = await _paymentValidator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _svc.AddPaymentAsync(caseSqid, body, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Created($"/api/insolvency/{caseSqid}/payments/{result.Value.Id}", result.Value)
            : MapFailureGeneric<InsolvencyPaymentDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Lists every payment row registered against the supplied case.</summary>
    /// <param name="caseSqid">Sqid-encoded id of the parent case.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the payment rows; 404 when the case is unknown.</returns>
    [HttpGet("{caseSqid}/payments")]
    public async Task<ActionResult<IReadOnlyList<InsolvencyPaymentDto>>> ListPaymentsAsync(
        [FromRoute] string caseSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ListPaymentsAsync(caseSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<IReadOnlyList<InsolvencyPaymentDto>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a generic <see cref="Result{T}"/> failure to an <see cref="ActionResult{T}"/>.</summary>
    /// <typeparam name="T">The DTO type the action would have returned on success.</typeparam>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 409 / 403 / 401 / 400 as appropriate.</returns>
    private ActionResult<T> MapFailureGeneric<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Maps a non-generic <see cref="Result"/> failure to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 409 / 403 / 401 / 400 as appropriate.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>Mapped HTTP status code.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Conflict => StatusCodes.Status409Conflict,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
