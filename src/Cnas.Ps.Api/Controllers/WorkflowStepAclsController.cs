using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.WorkflowAcl;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0126 / CF 16.10 — admin REST surface over the per-workflow per-step ACL registry.
/// Restricted to the <see cref="AuthorizationComposition.CnasAdmin"/> policy
/// (workflow definition management mirrors
/// <see cref="WorkflowNotificationStrategiesController"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>GET    /api/workflow-definitions/{workflowSqid}/step-acls</c>               — list step ACLs for the workflow.</item>
///   <item><c>PUT    /api/workflow-definitions/{workflowSqid}/step-acls/{stepCode}</c>    — idempotent upsert.</item>
///   <item><c>DELETE /api/workflow-definitions/{workflowSqid}/step-acls/{stepCode}</c>    — soft-delete.</item>
/// </list>
/// </para>
/// <para>
/// <b>Sqid convention.</b> <c>{workflowSqid}</c> is the Sqid-encoded
/// <c>WorkflowDefinition.Id</c> per CLAUDE.md RULE 3. The step-ACL's surrogate id is
/// Sqid-encoded inside the body DTO; the route never exposes it because the
/// (workflow, step code) natural key is the canonical handle.
/// </para>
/// </remarks>
/// <param name="svc">Underlying step-ACL service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/workflow-definitions/{workflowSqid}/step-acls")]
public sealed class WorkflowStepAclsController(IWorkflowStepAclService svc)
    : ControllerBase
{
    private readonly IWorkflowStepAclService _svc = svc;

    /// <summary>
    /// Lists every active step ACL bound to the workflow identified by
    /// <paramref name="workflowSqid"/>, ordered by step code ascending. Soft-deleted
    /// rows are excluded.
    /// </summary>
    /// <param name="workflowSqid">Sqid-encoded workflow definition id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the list on success; 400 on bad Sqid; 401 when anonymous.</returns>
    [HttpGet]
    public async Task<IActionResult> ListAsync(
        [FromRoute] string workflowSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ListAsync(workflowSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Idempotent upsert for the (<paramref name="workflowSqid"/>,
    /// <paramref name="stepCode"/>) ACL. Inserts on first call and updates thereafter;
    /// both paths trigger a Critical <c>WORKFLOW.STEP_ACL.{CREATED|UPDATED}</c> audit
    /// row.
    /// </summary>
    /// <param name="workflowSqid">Sqid-encoded workflow definition id.</param>
    /// <param name="stepCode">Canonical step code (BPMN activity-id form).</param>
    /// <param name="input">Upsert payload (body).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the resulting DTO; 400/404 on failure.</returns>
    [HttpPut("{stepCode}")]
    [Consumes("application/json")]
    public async Task<IActionResult> UpsertAsync(
        [FromRoute] string workflowSqid,
        [FromRoute] string stepCode,
        [FromBody] WorkflowStepAclUpsertInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _svc.UpsertAsync(workflowSqid, stepCode, input, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Soft-deletes the step ACL (flips <c>IsActive</c> to false). The row remains
    /// queryable for audit forensics.
    /// </summary>
    /// <param name="workflowSqid">Sqid-encoded workflow definition id.</param>
    /// <param name="stepCode">Canonical step code.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 404 when no row exists.</returns>
    [HttpDelete("{stepCode}")]
    public async Task<IActionResult> DeleteAsync(
        [FromRoute] string workflowSqid,
        [FromRoute] string stepCode,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.DeleteAsync(workflowSqid, stepCode, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailure(result.ErrorCode, result.ErrorMessage);
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
