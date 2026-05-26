using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Security;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// Server-to-server callback surface hit by MPay during the citizen payment ceremony.
/// Hosts two endpoints:
/// <list type="bullet">
///   <item>
///     <c>GET /api/mpay/orders/{orderId}/details</c> — MPay calls this immediately
///     before showing the payment page to the citizen, to fetch the current unpaid
///     amount + descriptor for the order. Returns 404 when the order is unknown.
///   </item>
///   <item>
///     <c>POST /api/mpay/orders/{orderId}/confirm</c> — MPay calls this once the
///     payment has settled, to inform CNAS the order is now confirmed. Idempotent —
///     a retried POST with the same <c>(orderId, paymentRef)</c> tuple succeeds without
///     side effects (CLAUDE.md cross-cutting "Idempotent Callbacks"). A retried POST
///     with a DIFFERENT payment reference on an already-confirmed order returns 409
///     so the divergence surfaces in operations dashboards instead of being silently
///     overwritten.
///   </item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Both endpoints are <see cref="AllowAnonymousAttribute"/> because MPay authenticates
/// itself at the transport layer (mTLS / source-IP allow-listing at the gateway), not
/// via an <c>Authorization</c> header. A future iteration will pin the MPay source
/// certificate; until then the trust boundary is the gateway configuration.
/// </para>
/// <para>
/// Persistence is owned by <see cref="IMPayOrderStore"/>. The store enforces
/// idempotency: re-applying the same <c>(orderId, paymentRef)</c> is a no-op success,
/// and a conflicting retry with a different <c>paymentRef</c> returns
/// <see cref="ErrorCodes.Conflict"/> which this controller maps to HTTP 409.
/// </para>
/// </remarks>
/// <param name="logger">
/// Structured logger; receives the order id + payment ref but NEVER the citizen's bank
/// credentials or the beneficiary IDNP (PII per TOR SEC 035 — the IDNP travels in the
/// 200 response body but is never written to a log line).
/// </param>
/// <param name="store">Persistence façade for MPay orders — see <see cref="IMPayOrderStore"/>.</param>
/// <param name="signatureVerifier">HMAC verifier for the anonymous MPay callback surface.</param>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingPolicies.Callback)]
[Route("api/mpay")]
public sealed class MPayCallbackController(
    ILogger<MPayCallbackController> logger,
    IMPayOrderStore store,
    ICallbackSignatureVerifier signatureVerifier) : ControllerBase
{
    private readonly ILogger<MPayCallbackController> _logger = logger;
    private readonly IMPayOrderStore _store = store;
    private readonly ICallbackSignatureVerifier _signatureVerifier = signatureVerifier;

    /// <summary>
    /// Quotes the unpaid amount + descriptor for the supplied order. Called by MPay
    /// just before it shows the payment page to the citizen.
    /// </summary>
    /// <param name="orderId">
    /// The CNAS-side order identifier originally posted to MPay via
    /// <see cref="Cnas.Ps.Application.Abstractions.IMPayClient.PostOrderAsync"/>. Must
    /// be a non-empty string; an empty value produces <c>400 Bad Request</c>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token honoured by the request pipeline.</param>
    /// <returns>
    /// <c>200 OK</c> with the canonical four-field JSON body
    /// (<c>orderId</c>, <c>amountMdl</c>, <c>descriptionRo</c>, <c>beneficiaryIdnp</c>)
    /// when the order is found; <c>404 Not Found</c> when no active row matches; <c>400</c>
    /// when the id is empty.
    /// </returns>
    [HttpGet("orders/{orderId}/details")]
    public async Task<IActionResult> GetOrderDetailsAsync(
        string orderId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return Problem("OrderId is required.", statusCode: 400);
        }

        var signature = _signatureVerifier.Verify(
            CallbackSignatureProvider.MPay,
            CanonicalDetailsPayload(orderId),
            Request.Headers);
        if (!signature.IsSuccess)
        {
            return Unauthorized(new { error = "invalid_callback_signature", detail = signature.ErrorMessage });
        }

        _logger.LogInformation("MPay GetOrderDetails called for order {OrderId}.", orderId);

        var snapshot = await _store.GetByOrderIdAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return NotFound();
        }

        var payload = new MPayOrderDetailsResponse(
            OrderId: snapshot.OrderId,
            AmountMdl: snapshot.AmountMdl,
            DescriptionRo: snapshot.DescriptionRo,
            BeneficiaryIdnp: snapshot.BeneficiaryIdnp);
        return Ok(payload);
    }

    /// <summary>
    /// Records a payment confirmation received from MPay. Idempotent — a retried POST
    /// with the same <paramref name="orderId"/> + <paramref name="request"/> payload
    /// is a no-op and returns <c>200 OK</c>. CLAUDE.md cross-cutting principle
    /// "Idempotent Callbacks".
    /// </summary>
    /// <param name="orderId">
    /// The CNAS-side order identifier the confirmation pertains to. Must be a
    /// non-empty string; an empty value produces <c>400 Bad Request</c>.
    /// </param>
    /// <param name="request">Confirmation payload — upstream payment reference + UTC timestamp.</param>
    /// <param name="cancellationToken">Cancellation token honoured by the request pipeline.</param>
    /// <returns>
    /// <c>200 OK</c> when the confirmation was accepted (first call and idempotent
    /// replays); <c>404 Not Found</c> when the order id is unknown; <c>409 Conflict</c>
    /// when the order is already confirmed with a different payment reference;
    /// <c>400 Bad Request</c> for an empty order id or any other failure code.
    /// </returns>
    [HttpPost("orders/{orderId}/confirm")]
    public async Task<IActionResult> ConfirmOrderPaymentAsync(
        string orderId,
        [FromBody] MPayConfirmRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return Problem("OrderId is required.", statusCode: 400);
        }

        // Structured logging — order id + payment ref only. The beneficiary IDNP is
        // deliberately omitted (PII per TOR SEC 035; lives in the persistence row, not
        // the log line).
        if (request is null)
        {
            return Problem("Request body is required.", statusCode: 400);
        }

        var signature = _signatureVerifier.Verify(
            CallbackSignatureProvider.MPay,
            CanonicalConfirmPayload(orderId, request),
            Request.Headers);
        if (!signature.IsSuccess)
        {
            return Unauthorized(new { error = "invalid_callback_signature", detail = signature.ErrorMessage });
        }

        _logger.LogInformation(
            "MPay confirmation received for order {OrderId}: paymentRef={PaymentRef}, confirmedAtUtc={ConfirmedAtUtc:o}.",
            orderId,
            request?.PaymentRef,
            request?.ConfirmedAtUtc);

        // Defensive guard on a missing body. The model binder normally produces a
        // non-null record because MPayConfirmRequest has positional required fields,
        // but treating a null body as a 400 keeps the error surface deterministic.
        if (request is null)
        {
            return Problem("Request body is required.", statusCode: 400);
        }

        var result = await _store
            .ConfirmAsync(orderId, request.PaymentRef, request.ConfirmedAtUtc, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? Ok() : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Maps a store-layer failure code to an HTTP response. Mirrors the helper pattern
    /// used by <see cref="WorkflowsController"/> / <see cref="TasksController"/>:
    /// <see cref="ErrorCodes.NotFound"/> → 404 (no body), <see cref="ErrorCodes.Conflict"/>
    /// → 409 ProblemDetails, everything else → 400 ProblemDetails.
    /// </summary>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>; may be <c>null</c>.</param>
    /// <param name="message">Human-readable detail forwarded into ProblemDetails.</param>
    private IActionResult MapFailure(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>
    /// Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code. The
    /// MPay callback surface only ever sees a small fixed set of codes — NotFound (the
    /// order is unknown) and Conflict (the order is already confirmed with a different
    /// payment reference). Anything else maps to 400 by convention.
    /// </summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    };

    private static string CanonicalDetailsPayload(string orderId) =>
        $"GET\n/api/mpay/orders/{orderId}/details";

    private static string CanonicalConfirmPayload(string orderId, MPayConfirmRequest request) =>
        string.Join(
            '\n',
            "POST",
            $"/api/mpay/orders/{orderId}/confirm",
            request.PaymentRef,
            request.ConfirmedAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>
/// Response body returned by <c>GET /api/mpay/orders/{orderId}/details</c>. Shape is
/// owned by MPay — every field name is the wire contract and must not be renamed.
/// </summary>
/// <param name="OrderId">Echo of the order id MPay asked about.</param>
/// <param name="AmountMdl">Unpaid amount in Moldovan Lei to collect from the citizen.</param>
/// <param name="DescriptionRo">Romanian descriptor shown on the MPay payment page.</param>
/// <param name="BeneficiaryIdnp">IDNP of the payer the order is bound to.</param>
public sealed record MPayOrderDetailsResponse(
    string OrderId,
    decimal AmountMdl,
    string DescriptionRo,
    string BeneficiaryIdnp);

/// <summary>
/// Request body MPay POSTs to <c>POST /api/mpay/orders/{orderId}/confirm</c> when a
/// payment settles. Must be idempotent — replaying the same payload is a no-op.
/// </summary>
/// <param name="PaymentRef">Upstream payment reference (typically a bank transaction id).</param>
/// <param name="ConfirmedAtUtc">UTC instant at which MPay recorded the confirmation.</param>
public sealed record MPayConfirmRequest(string PaymentRef, DateTime ConfirmedAtUtc);
