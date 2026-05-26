using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cnas.Ps.Api.Middleware;

/// <summary>
/// Catches anything that escapes the controller / endpoint pipeline, logs the full
/// exception (including stack trace) server-side with the request's correlation id, and
/// writes a sanitised <see cref="ProblemDetails"/> response. Stack traces NEVER appear on
/// the wire — they live in the server log only (SEC 057 / R0033).
/// </summary>
/// <remarks>
/// <para>
/// The contract:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       Production (<see cref="HostEnvironmentEnvExtensions.IsProduction(IHostEnvironment)"/>): respond with
///       <c>500</c> ProblemDetails, <c>errorCode = INTERNAL_ERROR</c>, a generic title,
///       NO <c>detail</c>, and the request's correlation id.
///     </description>
///   </item>
///   <item>
///     <description>
///       Non-production (Dev / Staging / CI): same shape PLUS <c>detail</c> with the
///       exception's <c>GetType().FullName + Message</c>. Still NO stack trace on the
///       wire — devs read the server log for the trace.
///     </description>
///   </item>
///   <item>
///     <description>
///       If the response has already started when the throw bubbles up, the middleware
///       cannot rewrite the wire envelope; it logs a distinct error and rethrows so the
///       host can abort the connection rather than emit a half-broken body.
///     </description>
///   </item>
/// </list>
/// <para>
/// The correlation id is sourced from <see cref="HttpContext.TraceIdentifier"/> — the
/// same source the rest of the system uses (see <c>HttpCallerContext.CorrelationId</c>),
/// so the wire envelope and server-side audit/log rows join on a single key.
/// </para>
/// <para>
/// The middleware MUST be registered first in the request pipeline so that exceptions
/// escaping authentication, authorization, routing, the rate limiter, MVC, etc. are all
/// captured. <see cref="Cnas.Ps.Api.Composition.ApiCompositionRoot.UseCnasApiPipeline"/>
/// places it ahead of every other middleware for that reason.
/// </para>
/// </remarks>
public sealed class UnhandledExceptionMiddleware
{
    /// <summary>Downstream pipeline delegate (the next middleware / endpoint).</summary>
    private readonly RequestDelegate _next;

    /// <summary>Logger used for the server-side stack trace; never written to the response.</summary>
    private readonly ILogger<UnhandledExceptionMiddleware> _logger;

    /// <summary>Host environment — drives the dev/prod branching of the response body.</summary>
    private readonly IWebHostEnvironment _env;

    /// <summary>
    /// Creates the middleware. The DI container resolves all three parameters.
    /// </summary>
    /// <param name="next">The next request delegate in the pipeline.</param>
    /// <param name="logger">Logger used to record exception detail server-side.</param>
    /// <param name="env">Host environment used to gate the dev-only <c>detail</c> field.</param>
    public UnhandledExceptionMiddleware(
        RequestDelegate next,
        ILogger<UnhandledExceptionMiddleware> logger,
        IWebHostEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(env);

        _next = next;
        _logger = logger;
        _env = env;
    }

    /// <summary>
    /// Invokes the pipeline and translates any escaped exception into a ProblemDetails
    /// response, or — if the response has already started — logs and rethrows.
    /// </summary>
    /// <param name="context">The current HTTP request context.</param>
    /// <returns>A task that completes when the response has been written.</returns>
    /// <remarks>
    /// The log line deliberately includes only the request method, the request path, and
    /// the correlation id. Query strings and the request body are NOT logged because both
    /// regularly carry citizen IDNPs and other PII (SEC 057). Headers — including
    /// <c>Authorization</c> — are likewise excluded.
    /// </remarks>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            var correlationId = context.TraceIdentifier;

            // Server-side log: full exception including stack trace. This line is the
            // ONLY place the stack appears — the wire envelope never carries it.
            _logger.LogError(
                ex,
                "Unhandled exception serving {Method} {Path} (correlationId={CorrelationId}).",
                context.Request.Method,
                context.Request.Path,
                correlationId);

            // Build a sanitised ProblemDetails. The Extensions dictionary carries the
            // stable error code (so machine clients can branch) plus the correlation id
            // (so humans reading the response can find the matching server-side log).
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Unexpected server error.",
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.1",
            };
            problem.Extensions["errorCode"] = ErrorCodes.Internal;
            problem.Extensions["correlationId"] = correlationId;

            // Non-production: include the exception type and message in `detail` to
            // shorten the dev feedback loop. Even here we DO NOT serialise the stack
            // trace — devs use the server log for traces.
            if (!_env.IsProduction())
            {
                problem.Detail = $"{ex.GetType().FullName}: {ex.Message}";
            }

            // Clear anything a downstream middleware may have started buffering before
            // the throw (e.g. partial headers from the rate limiter) and emit our envelope.
            // The `contentType` argument to WriteAsJsonAsync wins over the default
            // `application/json` it would otherwise stamp on the response.
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response
                .WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Response was already in flight when the throw escaped (e.g. a streaming
            // endpoint failed mid-body). We cannot rewrite a partially-emitted response,
            // so we log loudly and rethrow — the host will abort the connection.
            _logger.LogError(
                ex,
                "Unhandled exception AFTER response started for {Method} {Path} (correlationId={CorrelationId}); cannot rewrite.",
                context.Request.Method,
                context.Request.Path,
                context.TraceIdentifier);
            throw;
        }
    }
}
