using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abac;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Cnas.Ps.Api.Authorization;

/// <summary>
/// R2271 / TOR SEC 025 — ASP.NET Core authorization requirement that pairs with
/// <see cref="AbacAuthorizationHandler"/>. A single requirement instance is
/// created per <c>[Authorize(Policy="abac:NAME")]</c> attribute by the
/// <see cref="AbacPolicyProvider"/>.
/// </summary>
/// <param name="PolicyName">The stable rule-set policy name to dispatch against.</param>
public sealed record AbacRequirement(string PolicyName) : IAuthorizationRequirement;

/// <summary>
/// R2271 / TOR SEC 025 — authorization handler that resolves an
/// <see cref="AbacRequirement"/> by building an <see cref="AbacEvaluationContext"/>
/// from the current HTTP request, dispatching to
/// <see cref="IAbacRuleEvaluator"/>, and only calling
/// <see cref="AuthorizationHandlerContext.Succeed"/> when the verdict is
/// <see cref="AbacEffect.Allow"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Safe-by-default.</b> Evaluator failure (rule set not found, parse error
/// propagated up, etc.) is treated as "no Allow verdict" — the handler does
/// NOT call Succeed, the framework rejects the request. A failure to evaluate
/// must never grant access.
/// </para>
/// <para>
/// <b>Context assembly.</b> Subject attributes come from the
/// <see cref="ClaimsPrincipal"/> claims; resource attributes come from the
/// endpoint metadata (controller / action name); environment attributes carry
/// the current hour from the injected <see cref="ICnasTimeProvider"/>; action
/// attributes carry the HTTP method + path. Per-endpoint custom enrichment is
/// achievable in future iterations by adding a contributor seam — out of
/// scope for this iteration.
/// </para>
/// </remarks>
public sealed class AbacAuthorizationHandler : AuthorizationHandler<AbacRequirement>
{
    private readonly IAbacRuleEvaluator _evaluator;
    private readonly ICnasTimeProvider _clock;
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>Constructs the handler with its scoped collaborators.</summary>
    /// <param name="evaluator">Shared ABAC evaluator.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="httpContextAccessor">HTTP context accessor used to harvest endpoint metadata.</param>
    public AbacAuthorizationHandler(
        IAbacRuleEvaluator evaluator,
        ICnasTimeProvider clock,
        IHttpContextAccessor httpContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        _evaluator = evaluator;
        _clock = clock;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AbacRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var ct = _httpContextAccessor.HttpContext?.RequestAborted ?? default;
        var evaluationContext = BuildEvaluationContext(context.User);
        var decision = await _evaluator.EvaluateAsync(requirement.PolicyName, evaluationContext, ct).ConfigureAwait(false);
        if (decision.IsFailure)
        {
            // Evaluator failure (e.g. AbacNotFound) → DO NOT succeed; framework
            // will surface 403 by default.
            return;
        }
        if (string.Equals(decision.Value.Effect, AbacEffect.Allow.ToString(), StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }
    }

    /// <summary>
    /// Assembles an <see cref="AbacEvaluationContext"/> from the current HTTP
    /// request + claims principal.
    /// </summary>
    /// <param name="principal">The calling claims principal.</param>
    /// <returns>The assembled evaluation context.</returns>
    private AbacEvaluationContext BuildEvaluationContext(ClaimsPrincipal principal)
    {
        var http = _httpContextAccessor.HttpContext;

        var subject = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (principal?.Identity is { IsAuthenticated: true })
        {
            subject["userId"] = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            subject["roles"] = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        }

        var resource = new Dictionary<string, object?>(StringComparer.Ordinal);
        var actionDict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (http is not null)
        {
            var endpoint = http.GetEndpoint();
            var descriptor = endpoint?.Metadata.GetMetadata<ControllerActionDescriptor>();
            if (descriptor is not null)
            {
                resource["controllerName"] = descriptor.ControllerName;
                resource["actionName"] = descriptor.ActionName;
            }
            actionDict["method"] = http.Request.Method;
            actionDict["path"] = http.Request.Path.Value;
        }

        var environment = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["utcHour"] = (decimal)_clock.UtcNow.Hour,
            ["localHour"] = (decimal)_clock.UtcNow.Hour,
            ["dayOfWeek"] = _clock.UtcNow.DayOfWeek.ToString(),
        };

        return new AbacEvaluationContext(subject, resource, environment, actionDict);
    }
}
