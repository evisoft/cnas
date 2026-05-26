using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Cnas.Ps.Application.Sensitivity;
using Cnas.Ps.Contracts.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace Cnas.Ps.Api.Middleware;

/// <summary>
/// R0228 / TOR SEC 033 — middleware that stamps every response with the
/// <c>X-CNAS-Sensitivity</c> header (one of <c>Public</c>, <c>Internal</c>,
/// <c>Confidential</c>, <c>Restricted</c>) reflecting the highest
/// <see cref="SensitivityLabel"/> across the response payload, and writes a
/// <c>SENSITIVITY.RESTRICTED_ACCESS</c> audit row once per request when any
/// Restricted field is present.
/// </summary>
/// <remarks>
/// <para>
/// <b>How the response type is discovered.</b> The middleware reads
/// <see cref="IProducesResponseTypeMetadata"/> off the matched <see cref="Endpoint"/>
/// (status 200 entry preferred, otherwise the highest annotated success status). For
/// minimal APIs <c>Produces&lt;T&gt;()</c> attaches this metadata; for controllers
/// <c>[ProducesResponseType(typeof(T), 200)]</c> does the same. Endpoints that omit
/// the metadata fall through to the <see cref="SensitivityLabel.Internal"/> safety
/// floor — non-DTO results such as <see cref="IResult"/> file downloads land here.
/// </para>
/// <para>
/// <b>Collection unwrap.</b> A response type of <c>List&lt;T&gt;</c> or
/// <c>IReadOnlyCollection&lt;T&gt;</c> is unwrapped to <c>T</c> so the labels reflect
/// the element shape, not the envelope.
/// </para>
/// <para>
/// <b>Header timing.</b> Headers are populated through
/// <see cref="HttpResponse.OnStarting(Func{object, Task}, object)"/> so the
/// middleware can run BEFORE the endpoint body is flushed; this preserves
/// minimal-API patterns where the endpoint immediately begins writing the body.
/// </para>
/// <para>
/// <b>Single audit row per request.</b> Even when a payload exposes N Restricted
/// fields, exactly one <c>SENSITIVITY.RESTRICTED_ACCESS</c> audit row is written —
/// the row's payload lists every disclosed field.
/// </para>
/// </remarks>
public sealed class SensitivityHeaderMiddleware
{
    /// <summary>Header carrying the highest <see cref="SensitivityLabel"/> on the response.</summary>
    public const string HeaderName = "X-CNAS-Sensitivity";

    /// <summary>Header listing the comma-separated names of every Restricted field on the response.</summary>
    public const string FieldsHeaderName = "X-CNAS-Sensitivity-Fields";

    private readonly RequestDelegate _next;

    /// <summary>
    /// Creates the middleware.
    /// </summary>
    /// <param name="next">Next request delegate in the pipeline.</param>
    public SensitivityHeaderMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>
    /// Invokes the pipeline. Registers an <see cref="HttpResponse.OnStarting(Func{object, Task}, object)"/>
    /// callback that, just before the response headers are flushed, computes the labels
    /// and writes the <c>X-CNAS-Sensitivity</c> headers (auditing Restricted disclosures
    /// once per request).
    /// </summary>
    /// <param name="context">Current request context.</param>
    /// <param name="resolver">DI-resolved sensitivity resolver.</param>
    /// <param name="audit">DI-resolved sensitivity audit facade.</param>
    /// <returns>A task that completes when the pipeline finishes.</returns>
    public Task InvokeAsync(
        HttpContext context,
        ISensitivityResolver resolver,
        ISensitivityAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(audit);

        // Capture state in a closure-friendly tuple so OnStarting does NOT allocate a
        // delegate on every request once the JIT has stabilised.
        var state = new HeaderState(context, resolver, audit);
        context.Response.OnStarting(WriteHeadersAsync, state);

        return _next(context);
    }

    /// <summary>
    /// <see cref="HttpResponse.OnStarting(Func{object, Task}, object)"/> callback that
    /// computes labels from the matched endpoint and stamps the response headers.
    /// </summary>
    /// <param name="rawState">Opaque <see cref="HeaderState"/> wrapped by the runtime.</param>
    /// <returns>The audit task (a no-op when no Restricted field is exposed).</returns>
    private static async Task WriteHeadersAsync(object rawState)
    {
        var state = (HeaderState)rawState;
        var context = state.Context;

        // Discover the response DTO type via Produces metadata. When absent, default to
        // Internal — the safe floor for file downloads, redirects, problem details, etc.
        var dtoType = ResolveResponseType(context.GetEndpoint());
        if (dtoType is null)
        {
            context.Response.Headers[HeaderName] = SensitivityLabel.Internal.ToString();
            return;
        }

        var labels = state.Resolver.ResolveAll(dtoType);
        var highest = state.Resolver.Resolve(dtoType);

        context.Response.Headers[HeaderName] = highest.ToString();

        if (highest == SensitivityLabel.Restricted)
        {
            // Surface ONLY the Restricted-labelled property names — client-side audit
            // hooks need just the disclosed fields, not every property on the DTO.
            var restrictedFields = labels
                .Where(kv => kv.Value == SensitivityLabel.Restricted)
                .Select(kv => kv.Key)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            if (restrictedFields.Length > 0)
            {
                context.Response.Headers[FieldsHeaderName] = string.Join(",", restrictedFields);
            }

            // Exactly one audit row per request — the payload itself lists every disclosed
            // field. The resource name is the DTO type's friendly name so a future audit
            // explorer can group disclosures by surface.
            await state.Audit.RecordRestrictedAccessAsync(
                resource: dtoType.Name,
                recordSqid: null,
                propertyNames: restrictedFields,
                ct: context.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the response payload type from the matched endpoint's
    /// <see cref="IProducesResponseTypeMetadata"/>. Unwraps generic
    /// <see cref="System.Collections.Generic.IEnumerable{T}"/>-style envelopes so the
    /// labels reflect the element shape.
    /// </summary>
    /// <param name="endpoint">The matched endpoint, or null when no route bound.</param>
    /// <returns>The DTO type, or <c>null</c> when the endpoint has no Produces metadata.</returns>
    private static Type? ResolveResponseType(Endpoint? endpoint)
    {
        if (endpoint is null)
        {
            return null;
        }

        // Prefer the 200-status entry; otherwise the first success entry. Endpoints can
        // produce multiple types (e.g. 200 + 404 ProblemDetails) so we filter explicitly.
        var produces = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>();
        if (produces is null || produces.Count == 0)
        {
            return null;
        }

        var preferred = produces.FirstOrDefault(p => p.StatusCode == StatusCodes.Status200OK)
            ?? produces.FirstOrDefault(p => p.StatusCode >= 200 && p.StatusCode < 300);

        var dtoType = preferred?.Type;
        if (dtoType is null || dtoType == typeof(void))
        {
            return null;
        }

        return UnwrapCollection(dtoType);
    }

    /// <summary>
    /// Strips a top-level collection envelope so the labels read off the element type.
    /// Recognises <see cref="System.Collections.Generic.IEnumerable{T}"/>,
    /// <see cref="IReadOnlyCollection{T}"/>, <see cref="IReadOnlyList{T}"/>,
    /// <see cref="List{T}"/>, and arrays.
    /// </summary>
    /// <param name="type">Type to unwrap.</param>
    /// <returns>The element type when wrapped, else the input type.</returns>
    private static Type UnwrapCollection(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType() ?? type;
        }

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(List<>)
                || def == typeof(IList<>)
                || def == typeof(IReadOnlyList<>)
                || def == typeof(IReadOnlyCollection<>)
                || def == typeof(IEnumerable<>)
                || def == typeof(ICollection<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        // Walk implemented interfaces in case the type implements IEnumerable<T> via
        // composition (e.g. PagedResult<T>.Items). The first generic IEnumerable<T> wins.
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return iface.GetGenericArguments()[0];
            }
        }

        return type;
    }

    /// <summary>Captures the per-request services + context for the OnStarting callback.</summary>
    private sealed record HeaderState(
        HttpContext Context,
        ISensitivityResolver Resolver,
        ISensitivityAuditService Audit);
}

/// <summary>
/// R0228 / TOR SEC 033 — convenience extension that mounts
/// <see cref="SensitivityHeaderMiddleware"/> on the request pipeline. Call after
/// routing (so <c>HttpContext.GetEndpoint()</c> is populated) but before MVC /
/// minimal-API endpoint invocation so the <c>OnStarting</c> hook attaches before the
/// endpoint begins writing the response body.
/// </summary>
public static class SensitivityHeaderMiddlewareExtensions
{
    /// <summary>
    /// Mounts the <see cref="SensitivityHeaderMiddleware"/>.
    /// </summary>
    /// <param name="app">Application builder.</param>
    /// <returns>The same <paramref name="app"/> for fluent chaining.</returns>
    public static IApplicationBuilder UseSensitivityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SensitivityHeaderMiddleware>();
    }
}
