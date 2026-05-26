using System;
using System.Linq;
using System.Threading.Tasks;
using Cnas.Ps.Application.Permissions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.Api.Filters;

/// <summary>
/// R0673 / TOR CF 18.12 — declarative attribute that wires an MVC action through
/// the <see cref="IGranularPermissionService"/> matrix. The attribute carries the
/// stable <c>(Resource, Verb)</c> pair the action requires; the attribute
/// itself implements <see cref="IAsyncActionFilter"/> and consults
/// <see cref="IGranularPermissionService.HasPermissionAsync"/> for any role the
/// caller carries and short-circuits to 403 ProblemDetails when none match.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composes with role-based gates.</b> The attribute does NOT replace the
/// coarse <c>[Authorize(Policy=...)]</c> on the controller; it layers a
/// per-resource / per-verb gate on top. Unauthenticated callers are stopped
/// by the policy first and never reach the filter.
/// </para>
/// <para>
/// <b>Deny-by-default.</b> When the caller carries no roles, or every role is
/// missing the grant, the filter answers 403. Per CLAUDE.md §5.4.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class GranularPermissionAttribute : Attribute, IAsyncActionFilter
{
    /// <summary>Resource discriminator (e.g. <c>Dossier</c>).</summary>
    public string Resource { get; }

    /// <summary>Verb (from <c>PermissionVerbs</c>).</summary>
    public string Verb { get; }

    /// <summary>
    /// Constructs the attribute with the required (resource, verb) pair.
    /// </summary>
    /// <param name="resource">Resource discriminator.</param>
    /// <param name="verb">Verb name.</param>
    public GranularPermissionAttribute(string resource, string verb)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        Resource = resource;
        Verb = verb;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var services = context.HttpContext.RequestServices;
        var perms = services.GetRequiredService<IGranularPermissionService>();

        var user = context.HttpContext.User;
        var roles = user?.Claims
            .Where(c => string.Equals(c.Type, System.Security.Claims.ClaimTypes.Role, StringComparison.Ordinal)
                || string.Equals(c.Type, "role", StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Type, "roles", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (roles is null || roles.Count == 0)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = "Caller has no roles for granular permission check.",
            })
            { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        foreach (var role in roles)
        {
            var probe = await perms.HasPermissionAsync(role, Resource, Verb, context.HttpContext.RequestAborted)
                .ConfigureAwait(false);
            if (probe.IsSuccess && probe.Value)
            {
                await next().ConfigureAwait(false);
                return;
            }
        }

        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Forbidden",
            Detail = $"None of the caller's roles is granted '{Verb}' on '{Resource}'.",
        })
        { StatusCode = StatusCodes.Status403Forbidden };
    }
}
