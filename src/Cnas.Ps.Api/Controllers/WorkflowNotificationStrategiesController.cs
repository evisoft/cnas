using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.WorkflowNotifications;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0128 / R0173 / CF 16.14 + CF 22.04 — admin REST surface over the per-workflow
/// notification-strategy registry. Restricted to the
/// <see cref="AuthorizationComposition.CnasAdmin"/> policy (workflow definition
/// management, mirroring <see cref="WorkflowsController"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>GET    /api/workflow-definitions/{workflowSqid}/notification-strategies</c>                — list strategies for the workflow.</item>
///   <item><c>GET    /api/workflow-definitions/{workflowSqid}/notification-strategies/by-event/{eventCode}</c> — fetch one strategy.</item>
///   <item><c>PUT    /api/workflow-definitions/{workflowSqid}/notification-strategies/{eventCode}</c>     — idempotent upsert.</item>
///   <item><c>DELETE /api/workflow-definitions/{workflowSqid}/notification-strategies/{eventCode}</c>     — soft-disable.</item>
/// </list>
/// </para>
/// <para>
/// <b>Sqid convention.</b> <c>{workflowSqid}</c> is the Sqid-encoded
/// <c>WorkflowDefinition.Id</c> per CLAUDE.md RULE 3. The strategy's own surrogate id
/// is Sqid-encoded inside the body DTO; the route never exposes it because the
/// (workflow, event code) natural key is the canonical handle.
/// </para>
/// </remarks>
/// <param name="svc">Underlying strategy service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/workflow-definitions/{workflowSqid}/notification-strategies")]
public sealed class WorkflowNotificationStrategiesController(IWorkflowNotificationStrategyService svc)
    : ControllerBase
{
    private readonly IWorkflowNotificationStrategyService _svc = svc;

    /// <summary>
    /// Lists every active strategy bound to the workflow identified by
    /// <paramref name="workflowSqid"/>, ordered by event code ascending. Disabled
    /// (soft-deleted) rows are excluded.
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
    /// Fetches the single strategy bound to (<paramref name="workflowSqid"/>,
    /// <paramref name="eventCode"/>).
    /// </summary>
    /// <param name="workflowSqid">Sqid-encoded workflow definition id.</param>
    /// <param name="eventCode">Canonical event code (e.g. <c>Task.Assigned</c>).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the row; 404 when not configured.</returns>
    [HttpGet("by-event/{eventCode}")]
    public async Task<IActionResult> GetByEventAsync(
        [FromRoute] string workflowSqid,
        [FromRoute] string eventCode,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.GetByEventAsync(workflowSqid, eventCode, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Idempotent upsert for the (<paramref name="workflowSqid"/>,
    /// <paramref name="eventCode"/>) strategy. Inserts on first call and updates
    /// thereafter; both paths trigger a Critical
    /// <c>WORKFLOW.NOTIFY.STRATEGY.{CREATED|UPDATED}</c> audit row.
    /// </summary>
    /// <param name="workflowSqid">Sqid-encoded workflow definition id.</param>
    /// <param name="eventCode">Canonical event code.</param>
    /// <param name="input">Upsert payload (body).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the resulting DTO; 400/404 on failure.</returns>
    [HttpPut("{eventCode}")]
    [Consumes("application/json")]
    public async Task<IActionResult> UpsertAsync(
        [FromRoute] string workflowSqid,
        [FromRoute] string eventCode,
        [FromBody] WorkflowNotificationStrategyUpsertInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _svc.UpsertAsync(workflowSqid, eventCode, input, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Soft-disables the strategy (flips <c>IsActive</c> to false). The row remains
    /// queryable for audit forensics; the explicit <c>IsEnabled</c> "do not notify"
    /// override flag is NOT touched here.
    /// </summary>
    /// <param name="workflowSqid">Sqid-encoded workflow definition id.</param>
    /// <param name="eventCode">Canonical event code.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 404 when not configured.</returns>
    [HttpDelete("{eventCode}")]
    public async Task<IActionResult> DisableAsync(
        [FromRoute] string workflowSqid,
        [FromRoute] string eventCode,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.DisableAsync(workflowSqid, eventCode, cancellationToken)
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
