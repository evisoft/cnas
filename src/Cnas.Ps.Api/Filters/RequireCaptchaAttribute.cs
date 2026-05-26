using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.Api.Filters;

/// <summary>
/// ASP.NET MVC action filter that gates the decorated endpoint behind a successful
/// <see cref="ICaptchaVerifier"/> check (R0035 — anonymous-surface abuse prevention,
/// UC01 / UC02). The filter sits in front of the controller action; on rejection it
/// writes a <c>application/problem+json</c> response with a stable
/// <see cref="ErrorCodes"/> code in the <c>errorCode</c> extension and never invokes
/// the action body.
/// </summary>
/// <remarks>
/// <para>
/// <b>Request contract.</b> Anonymous clients submit the CAPTCHA token through the
/// HTTP header <c>X-Captcha-Token</c>. The filter reads that header, hands the value
/// (plus the resolved remote IP — for Turnstile abuse correlation) to the registered
/// <see cref="ICaptchaVerifier"/>, and branches on the returned <see cref="Result"/>.
/// </para>
/// <para>
/// <b>Response contract.</b> Rejected requests return ProblemDetails JSON shaped like:
/// <code>
/// {
///   "status": 400,
///   "title": "CAPTCHA verification failed.",
///   "errorCode": "CAPTCHA_TOKEN_MISSING"
/// }
/// </code>
/// Status-code mapping per CLAUDE.md §2.2 + fail-closed posture (§5):
/// <list type="bullet">
///   <item><see cref="ErrorCodes.CaptchaTokenMissing"/> → HTTP 400.</item>
///   <item><see cref="ErrorCodes.CaptchaTokenInvalid"/> → HTTP 400.</item>
///   <item><see cref="ErrorCodes.CaptchaProviderUnreachable"/> → HTTP 503.</item>
///   <item>Any other failure code → HTTP 400 (defensive default).</item>
/// </list>
/// </para>
/// <para>
/// <b>Layering with rate limiting.</b> This filter is complementary to
/// <c>RateLimitingComposition.Anonymous</c>: the rate limiter runs first (defending
/// against per-IP volumetric abuse) and the CAPTCHA filter runs in MVC (defending
/// against single-call automation that rotates IPs). Both must pass before the action
/// body executes.
/// </para>
/// <para>
/// <b>Scope.</b> Apply at the CLASS level on anonymous controllers (e.g.
/// <c>PublicController</c>) so every action inherits the gate without per-method
/// repetition. Do NOT apply to authenticated endpoints — they already require a
/// valid auth cookie / token and the CAPTCHA step adds friction without value there.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class RequireCaptchaAttribute : Attribute, IAsyncActionFilter
{
    /// <summary>HTTP header carrying the CAPTCHA token from the client widget.</summary>
    public const string TokenHeaderName = "X-Captcha-Token";

    /// <inheritdoc />
    /// <remarks>
    /// Resolves <see cref="ICaptchaVerifier"/> from the request services (rather than
    /// constructor injection) because attributes cannot have DI-injected dependencies
    /// — ASP.NET instantiates them via <c>Activator.CreateInstance</c>. The verifier
    /// is registered as Scoped in <c>InfrastructureServiceCollectionExtensions</c>;
    /// resolving per-request is correct.
    /// </remarks>
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var verifier = context.HttpContext.RequestServices.GetRequiredService<ICaptchaVerifier>();

        var token = context.HttpContext.Request.Headers[TokenHeaderName].ToString();
        // Normalise to null when the header is absent / whitespace so the verifier can
        // short-circuit to CaptchaTokenMissing without an HTTP round-trip.
        var normalisedToken = string.IsNullOrWhiteSpace(token) ? null : token;

        var remoteIp = context.HttpContext.Connection.RemoteIpAddress?.ToString();

        var result = await verifier
            .VerifyAsync(normalisedToken, remoteIp, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            var status = MapStatusCode(result.ErrorCode);
            var problem = new ProblemDetails
            {
                Status = status,
                Title = "CAPTCHA verification failed.",
            };
            // ProblemDetails.Extensions serialises at the JSON root (it carries the
            // [JsonExtensionData] attribute on the property). Putting the stable error
            // code there mirrors the convention used by UnhandledExceptionMiddleware
            // and the rate-limiter rejection responder.
            problem.Extensions["errorCode"] = result.ErrorCode;

            context.Result = new ObjectResult(problem)
            {
                StatusCode = status,
                ContentTypes = { "application/problem+json" },
            };
            return;
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// Maps a CAPTCHA failure code to its HTTP status. Defaults to 400 for any
    /// unrecognised code — defensive against future additions that haven't yet been
    /// considered for HTTP-status mapping.
    /// </summary>
    /// <param name="errorCode">The <see cref="Result.ErrorCode"/> from the verifier.</param>
    /// <returns>HTTP status code to surface to the client.</returns>
    private static int MapStatusCode(string? errorCode) => errorCode switch
    {
        ErrorCodes.CaptchaTokenMissing => StatusCodes.Status400BadRequest,
        ErrorCodes.CaptchaTokenInvalid => StatusCodes.Status400BadRequest,
        ErrorCodes.CaptchaProviderUnreachable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    };
}
