using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// UC05 — "Execut sarcini" (TOR §3.5 / CF 05.01–05.06). REST surface over
/// <see cref="ITaskInboxService"/> for the workflow task inbox: list assigned work,
/// claim a group-inbox task, and complete an owned task with a result payload.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorization.</b> Gated by <see cref="AuthorizationComposition.CnasDecider"/>:
/// the inbox is the deciders' / examiners' day-to-day workspace (CF 05.01 — "Examinatorul
/// accesează lista de sarcini"). <see cref="AuthorizationComposition.CnasUser"/> would
/// be too permissive — read-only CNAS users should not see deciders' work queues — and
/// <see cref="AuthorizationComposition.CnasAdmin"/> would be too restrictive (admins
/// configure workflows but do not execute tasks). CnasDecider is the right fit and is
/// also satisfied transparently by CnasAdmin per the policy tier in
/// <c>AuthorizationComposition</c>.
/// </para>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>GET    /api/tasks?page=1&amp;pageSize=20</c>     — list the caller's inbox (CF 05.01).</item>
///   <item><c>POST   /api/tasks/{id}/claim</c>            — claim a group-inbox task (CF 05.02).</item>
///   <item><c>POST   /api/tasks/{id}/complete</c>         — complete an owned task with a result payload (CF 05.04).</item>
/// </list>
/// </para>
/// <para>
/// <b>Sqid convention.</b> The <c>{id}</c> route segment is a Sqid-encoded workflow-task
/// identifier (CLAUDE.md RULE 3). The controller forwards it verbatim to the service,
/// which decodes via <see cref="ISqidService.TryDecode"/> and surfaces
/// <see cref="ErrorCodes.InvalidSqid"/> for malformed values (mapped to 400 here).
/// </para>
/// <para>
/// <b>Error-code → HTTP status mapping.</b> Mirrors the pattern in
/// <see cref="WorkflowsController"/>: <see cref="ErrorCodes.NotFound"/> → 404,
/// <see cref="ErrorCodes.Forbidden"/> / <see cref="ErrorCodes.WorkflowNotAssignee"/> → 403,
/// <see cref="ErrorCodes.Unauthorized"/> → 401, all other codes → 400. Auditing of
/// task transitions, when introduced, is the responsibility of
/// <see cref="ITaskInboxService"/> — this controller adds no audit writes.
/// </para>
/// </remarks>
/// <param name="tasks">Underlying task-inbox service (UC05).</param>
/// <param name="sqids">Sqid encoder/decoder for the new-assignee Sqid carried in reassignment bodies.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasDecider)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/tasks")]
public sealed class TasksController(ITaskInboxService tasks, ISqidService sqids) : ControllerBase
{
    private readonly ITaskInboxService _tasks = tasks;
    private readonly ISqidService _sqids = sqids;

    /// <summary>
    /// Lists the caller's workflow inbox — tasks currently assigned to the calling user.
    /// Paged per TOR UI 014 / CF 01.06 ("results must be paged"); the service clamps
    /// <see cref="PageRequest.PageSize"/> to <c>[1, 200]</c> defensively.
    /// </summary>
    /// <param name="page">Pagination input bound from <c>?page=&amp;pageSize=</c>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 OK with a <see cref="PagedResult{T}"/> of <see cref="TaskInboxItem"/>; 401
    /// when the principal is anonymous (defense-in-depth — the policy gate normally
    /// blocks this earlier); 400 ProblemDetails for any other failure code.
    /// </returns>
    [HttpGet]
    public async Task<ActionResult<PagedResult<TaskInboxItem>>> ListAsync(
        [FromQuery] PageRequest page,
        CancellationToken cancellationToken = default)
    {
        // ArgumentNullException defensiveness lives on the service; the binder always
        // produces a non-null PageRequest because the record has default values.
        var result = await _tasks.ListAsync(page, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<PagedResult<TaskInboxItem>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Claims a group-inbox task — flips <c>AssignedUserId</c> to the calling user and
    /// transitions the task into <c>WorkflowTaskStatus.InProgress</c> (CF 05.02). A second
    /// user attempting to claim a task already assigned to another caller receives 403 /
    /// <see cref="ErrorCodes.WorkflowNotAssignee"/> — deny-by-default per CLAUDE.md §5.4.
    /// Re-claim by the same caller remains idempotent (still 204, still flips Status to
    /// <c>InProgress</c>).
    /// </summary>
    /// <param name="id">
    /// Sqid-encoded workflow-task identifier (route parameter). Forwarded verbatim to
    /// the service; malformed values surface as <see cref="ErrorCodes.InvalidSqid"/>
    /// → 400.
    /// </param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 204 No Content on success; 404 when the Sqid does not match an active task; 400
    /// for malformed Sqid; 403 when the service returns a forbidden code.
    /// </returns>
    [HttpPost("{id}/claim")]
    public async Task<IActionResult> ClaimAsync(
        [FromRoute] string id,
        CancellationToken cancellationToken = default)
    {
        var result = await _tasks.ClaimAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Completes a task owned by the caller. Transitions the row to
    /// <c>WorkflowTaskStatus.Completed</c> and stamps <c>CompletedAtUtc</c>; the
    /// supplied <see cref="CompleteTaskRequest.ResultJson"/> is forwarded verbatim to
    /// the service (CF 05.04). Returns 403 when the caller is not the assigned user
    /// (<see cref="ErrorCodes.WorkflowNotAssignee"/>).
    /// </summary>
    /// <param name="id">
    /// Sqid-encoded workflow-task identifier (route parameter). Forwarded verbatim to
    /// the service.
    /// </param>
    /// <param name="body">JSON body carrying the result envelope; see
    /// <see cref="CompleteTaskRequest"/>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 204 No Content on success; 404 / 400 / 403 ProblemDetails on failure per the
    /// error-code map in the controller remarks.
    /// </returns>
    [HttpPost("{id}/complete")]
    [Consumes("application/json")]
    public async Task<IActionResult> CompleteAsync(
        [FromRoute] string id,
        [FromBody] CompleteTaskRequest body,
        CancellationToken cancellationToken = default)
    {
        // ResultJson is the only field on the request DTO — treat null as empty so the
        // service receives a well-formed (but inert) payload rather than NRE-ing on a
        // missing body. The shape contract on CompleteTaskRequest documents that an
        // empty JSON object is acceptable.
        var payload = body?.ResultJson ?? string.Empty;
        var result = await _tasks.CompleteAsync(id, payload, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0127 / CF 16.11 — Reassigns the supplied task to a different user. Decodes the
    /// route Sqid + the body's new-assignee Sqid, validates the body via FluentValidation,
    /// and delegates to <see cref="ITaskInboxService.ReassignAsync"/>.
    /// </summary>
    /// <param name="taskSqid">Sqid-encoded workflow-task identifier (route parameter).</param>
    /// <param name="body">Reassignment request body — see <see cref="WorkflowTaskReassignDto"/>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 OK with the Sqid-encoded snapshot of the updated task; 400 on Sqid /
    /// validation failures; 403 when the new assignee is suspended; 404 when the task or
    /// new assignee is missing.
    /// </returns>
    [HttpPost("{taskSqid}/reassign")]
    [Consumes("application/json")]
    public async Task<ActionResult<WorkflowTaskOutputDto>> ReassignAsync(
        [FromRoute] string taskSqid,
        [FromBody] WorkflowTaskReassignDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        // ─── Validate body shape via FluentValidation. ───
        var validator = new WorkflowTaskReassignDtoValidator();
        var validation = await validator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        // ─── Decode both Sqids. ───
        var taskDecoded = _sqids.TryDecode(taskSqid);
        if (taskDecoded.IsFailure)
        {
            return Problem(taskDecoded.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }
        var newAssigneeDecoded = _sqids.TryDecode(body.NewAssigneeSqid);
        if (newAssigneeDecoded.IsFailure)
        {
            return Problem(newAssigneeDecoded.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _tasks.ReassignAsync(
            taskDecoded.Value,
            newAssigneeDecoded.Value,
            body.Reason,
            absenceId: null,
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<WorkflowTaskOutputDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0381 / UC05 — supervisor workspace queue. Lists active tasks assigned to
    /// peers of the calling supervisor (any user sharing a group). Gated by the
    /// <see cref="AuthorizationComposition.SefulDirectiei"/> policy — supervisors
    /// only.
    /// </summary>
    /// <param name="assignee">Optional Sqid filter on the task assignee.</param>
    /// <param name="page">1-based page index (default 1).</param>
    /// <param name="pageSize">Items per page (clamped server-side to [1, 50]).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 OK with a paged list of <see cref="SupervisorTeamTaskDto"/>; 400 on malformed
    /// assignee Sqid; 401/403 via the policy gate.
    /// </returns>
    [HttpGet("supervisor/team")]
    [Authorize(Policy = AuthorizationComposition.SefulDirectiei)]
    public async Task<ActionResult<PagedResult<SupervisorTeamTaskDto>>> ListTeamQueueAsync(
        [FromQuery] string? assignee = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _tasks.ListTeamQueueAsync(assignee, page, pageSize, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<PagedResult<SupervisorTeamTaskDto>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0127 / CF 16.11 — Reverts the supplied task's most recent reassignment, restoring
    /// the original assignee captured on first delegation. The task must still be open and
    /// must have been reassigned at least once.
    /// </summary>
    /// <param name="taskSqid">Sqid-encoded workflow-task identifier.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 204 No Content on success; 400 on malformed Sqid / not-reassigned; 404 when the
    /// task is missing.
    /// </returns>
    [HttpPost("{taskSqid}/revert-reassignment")]
    public async Task<IActionResult> RevertReassignmentAsync(
        [FromRoute] string taskSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(taskSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _tasks.RevertReassignmentAsync(decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? NoContent()
            : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Maps a non-generic <see cref="Result"/> failure to an <see cref="IActionResult"/>.
    /// Mirrors the helper in <see cref="WorkflowsController"/> so the two controllers
    /// share the same error-code → HTTP-status policy.
    /// </summary>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>; may be <c>null</c>.</param>
    /// <param name="message">Human-readable detail forwarded into ProblemDetails.</param>
    /// <returns>404 <see cref="NotFoundResult"/> for <see cref="ErrorCodes.NotFound"/>;
    /// otherwise a ProblemDetails ObjectResult at the appropriate status.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>
    /// Generic-arity twin of <see cref="MapFailureBare"/> for actions whose return type
    /// is <c>ActionResult&lt;T&gt;</c> (e.g. <see cref="ListAsync"/>). Keeps the
    /// branch logic in sync with the non-generic mapper.
    /// </summary>
    /// <typeparam name="T">Type-parameter of the action's <c>ActionResult&lt;T&gt;</c>.</typeparam>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>; may be <c>null</c>.</param>
    /// <param name="message">Human-readable detail forwarded into ProblemDetails.</param>
    /// <returns>404 <see cref="NotFoundResult"/> for <see cref="ErrorCodes.NotFound"/>;
    /// otherwise a ProblemDetails <see cref="ObjectResult"/> at the appropriate status.</returns>
    private ActionResult<T> MapFailureGeneric<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>
    /// Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.
    /// <see cref="ErrorCodes.WorkflowNotAssignee"/> is mapped to 403 because the caller
    /// is authenticated but lacks permission to complete a task they do not own
    /// (CLAUDE.md §5.4 — "deny by default").
    /// </summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>The mapped HTTP status code.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.WorkflowNotAssignee => StatusCodes.Status403Forbidden,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
