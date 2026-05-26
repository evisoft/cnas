using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Workflow;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0123 / TOR CF 16.05 — admin REST surface over the persisted workflow execution
/// graph (nodes + edges). Restricted to the
/// <see cref="AuthorizationComposition.CnasAdmin"/> policy because every successful
/// <c>PUT</c> mints a new <c>WorkflowDefinition</c> version (R0129) and writes a
/// Critical audit row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>GET /api/workflow-definitions/{workflowSqid}/graph</c> — returns the graph pinned to that version.</item>
///   <item><c>PUT /api/workflow-definitions/{workflowSqid}/graph</c> — replaces the graph; mints a new version and returns the new graph DTO.</item>
/// </list>
/// </para>
/// <para>
/// <b>Sqid convention.</b> <c>{workflowSqid}</c> is the Sqid-encoded
/// <c>WorkflowDefinition.Id</c> per CLAUDE.md RULE 3 — note the contrast with
/// <c>WorkflowsController</c> which uses the natural-key <c>Code</c> instead. We pick
/// the surrogate id here so historical version graphs are addressable (the Code is
/// shared across every version of the workflow chain).
/// </para>
/// </remarks>
/// <param name="svc">Underlying workflow-graph service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/workflow-definitions/{workflowSqid}/graph")]
public sealed class WorkflowGraphsAdminController(IWorkflowGraphService svc) : ControllerBase
{
    private readonly IWorkflowGraphService _svc = svc;

    /// <summary>
    /// Returns the persisted execution graph for the workflow definition identified by
    /// <paramref name="workflowSqid"/>. The lookup is by surrogate id, so a historical
    /// (superseded) version row's graph can be inspected by handing in the Sqid for
    /// that version explicitly.
    /// </summary>
    /// <param name="workflowSqid">Sqid-encoded workflow-definition id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the <see cref="WorkflowGraphDto"/>; 400 on bad Sqid; 404 when no row matches.</returns>
    [HttpGet]
    public async Task<IActionResult> GetAsync(
        [FromRoute] string workflowSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.GetForVersionAsync(workflowSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Replaces the persisted execution graph for the workflow definition identified by
    /// <paramref name="workflowSqid"/>. The replace is destructive: every existing node
    /// and edge row is superseded as part of the same transaction that mints a fresh
    /// <c>WorkflowDefinition</c> version (R0129).
    /// </summary>
    /// <param name="workflowSqid">Sqid-encoded workflow-definition id.</param>
    /// <param name="input">Replacement graph (nodes + edges).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the resulting <see cref="WorkflowGraphDto"/>; 400/404 on failure.</returns>
    [HttpPut]
    [Consumes("application/json")]
    public async Task<IActionResult> ReplaceAsync(
        [FromRoute] string workflowSqid,
        [FromBody] WorkflowGraphInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _svc.ReplaceGraphAsync(workflowSqid, input, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a service-layer failure code to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>The mapped ProblemDetails / NotFound action result.</returns>
    private IActionResult MapFailure(string? code, string? message)
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
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.Conflict => StatusCodes.Status409Conflict,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
