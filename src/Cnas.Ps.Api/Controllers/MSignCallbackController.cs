using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// Server-to-server callback endpoint hit by MSign when a signing request becomes
/// ready. MSign POSTs to <c>POST /api/msign/callback/{requestId}</c> immediately
/// after a citizen completes the signing ceremony in the MSign portal; the request
/// id matches the value returned from
/// <see cref="Cnas.Ps.Application.Abstractions.IMSignClient.PostSignRequestAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint is <see cref="AllowAnonymousAttribute"/> because MSign does not present
/// an end-user <c>Authorization</c> header. The handler still requires the shared
/// timestamped callback HMAC before accepting the request; ingress mTLS / source
/// allow-listing remains a defense-in-depth control. CNAS does not yet persist signing requests
/// (that lives in a future "SigningRequests" table — out of scope for the current
/// refactor); the minimal implementation logs the receipt as a structured event and
/// returns <c>200 OK</c>.
/// </para>
/// <para>
/// The handler is intentionally cheap and side-effect-free besides logging so that a
/// retry from MSign (which the upstream may issue if the first POST times out) is
/// safely idempotent — see CLAUDE.md cross-cutting principle "Idempotent Callbacks".
/// </para>
/// </remarks>
/// <param name="logger">Structured logger; receives the request id but never the signature bytes.</param>
/// <param name="signatureVerifier">Shared HMAC verifier for anonymous MGov callbacks.</param>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingPolicies.Callback)]
[Route("api/msign/callback")]
public sealed class MSignCallbackController(
    ILogger<MSignCallbackController> logger,
    ICallbackSignatureVerifier signatureVerifier) : ControllerBase
{
    private readonly ILogger<MSignCallbackController> _logger = logger;
    private readonly ICallbackSignatureVerifier _signatureVerifier = signatureVerifier;

    /// <summary>
    /// Receives the MSign-issued readiness callback for the supplied request id.
    /// </summary>
    /// <param name="requestId">
    /// The MSign-allocated request id allocated by the original PostSignRequest call.
    /// Must be a non-empty string; an empty or whitespace value produces
    /// <c>400 Bad Request</c>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token honoured by the request pipeline.</param>
    /// <returns>
    /// <c>200 OK</c> when the callback was accepted; <c>400 Bad Request</c> when the
    /// supplied request id is empty.
    /// </returns>
    [HttpPost("{requestId}")]
    public Task<IActionResult> CallbackAsync(string requestId, CancellationToken cancellationToken = default)
    {
        // Bound the cancellation token early so a long-running pipeline can still bail.
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(requestId))
        {
            return Task.FromResult<IActionResult>(Problem("RequestId is required.", statusCode: 400));
        }

        var signature = _signatureVerifier.Verify(
            CallbackSignatureProvider.MSign,
            requestId,
            Request.Headers);
        if (!signature.IsSuccess)
        {
            return Task.FromResult<IActionResult>(Unauthorized(new { error = signature.ErrorMessage }));
        }

        _logger.LogInformation("MSign callback received for request {RequestId}.", requestId);
        return Task.FromResult<IActionResult>(Ok());
    }
}
