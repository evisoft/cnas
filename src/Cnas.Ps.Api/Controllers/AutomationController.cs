using System.Text.Json;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// UC20 — "Execut proceduri automate" (execute automated procedures). REST surface over
/// <see cref="IAutomationService"/>. Technical administrators
/// (<see cref="AuthorizationComposition.CnasTechAdmin"/> policy) trigger named
/// automations on-demand and update their cron schedules. Distinct from
/// <see cref="AdminController"/>'s failed-job DLQ — that controller surfaces the
/// aftermath of a job crash; this one is the forward control surface for the same job
/// scheduler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Automation code semantics.</b> Like workflow codes, the <c>code</c> route segment
/// is NOT a Sqid (CLAUDE.md RULE 3 does NOT apply). Automation codes are stable Quartz
/// job names (e.g. <c>mpay-dispatcher</c>, <c>mpower-housekeeping</c>) chosen by the
/// platform team — they ARE the public name of the job. Sqid-encoding would obscure the
/// very identifier operators use in runbooks.
/// </para>
/// <para>
/// Route table:
/// <list type="bullet">
///   <item><c>POST /api/automation/{code}/run-now</c>  — fire the automation immediately (202 / 400 / 404).</item>
///   <item><c>PUT  /api/automation/{code}/schedule</c> — update the cron schedule (204 / 400 / 404).</item>
/// </list>
/// </para>
/// </remarks>
/// <param name="automation">Underlying automation service.</param>
/// <param name="inspector">
/// R0204 / TOR CF 20.07-08 — read-only scheduler inspector used by
/// <see cref="ListJobStatesAsync"/> to project Quartz job + trigger state for the
/// admin Jobs dashboard.
/// </param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasTechAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/automation")]
public sealed class AutomationController(
    IAutomationService automation,
    IJobStateInspector inspector) : ControllerBase
{
    private readonly IAutomationService _automation = automation;
    private readonly IJobStateInspector _inspector = inspector;

    /// <summary>
    /// Fires the automation named <paramref name="code"/> immediately. The optional
    /// <paramref name="body"/> carries a parameter dictionary that is serialised to a
    /// JSON object and forwarded to <see cref="IAutomationService.RunNowAsync"/> as the
    /// Quartz JobDataMap payload. A null body is permitted (e.g. for parameterless jobs)
    /// and the controller forwards the literal <c>"{}"</c> in that case so the service
    /// sees an empty parameter map rather than a parse error.
    /// </summary>
    /// <remarks>
    /// The underlying service returns a non-generic <see cref="Result"/> with no payload
    /// — there is no synchronous "run id" to surface because Quartz schedules the job
    /// fire-and-forget. The controller therefore returns 202 Accepted on success rather
    /// than 200 OK; the caller polls <see cref="AdminController.ListFailedJobsAsync"/> or
    /// the audit log to confirm completion.
    /// </remarks>
    /// <param name="code">Stable automation code (Quartz job name, NOT a Sqid).</param>
    /// <param name="body">Optional parameter dictionary; null is treated as an empty map.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>202 Accepted on success; 400 / 404 ProblemDetails on failure.</returns>
    [HttpPost("{code}/run-now")]
    public async Task<IActionResult> RunNowAsync(
        [FromRoute] string code,
        [FromBody] AutomationRunNowRequest? body,
        CancellationToken cancellationToken = default)
    {
        var parametersJson = SerialiseParameters(body?.Parameters);
        var result = await _automation.RunNowAsync(code, parametersJson, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Accepted()
            : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Updates the cron schedule for the automation named <paramref name="code"/>. The new
    /// cron expression is parsed and validated by the service; malformed expressions
    /// surface as <see cref="ErrorCodes.ValidationFailed"/> → 400. The change takes effect
    /// at the next scheduler re-load (the service is responsible for hot-reload).
    /// </summary>
    /// <param name="code">Stable automation code (Quartz job name, NOT a Sqid).</param>
    /// <param name="body">Schedule payload carrying the new cron expression.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 No Content on success; 400 / 404 ProblemDetails on failure.</returns>
    [HttpPut("{code}/schedule")]
    public async Task<IActionResult> ScheduleAsync(
        [FromRoute] string code,
        [FromBody] AutomationScheduleRequest body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await _automation.ScheduleAsync(code, body.CronExpression, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0204 / TOR CF 20.07-08 — returns the current state of every Quartz job + trigger
    /// registered with the running scheduler. Consumed by the admin "Jobs dashboard"
    /// Blazor page (<c>JobsDashboard.razor</c>) so technical administrators can see, at a
    /// glance, which background jobs exist, when they last fired, when they next fire,
    /// and whether they are currently paused.
    /// </summary>
    /// <remarks>
    /// The endpoint is GET / idempotent and read-only — calling it never mutates
    /// scheduler state. Authorisation is the same <see cref="AuthorizationComposition.CnasTechAdmin"/>
    /// policy that gates the rest of this controller; the inspector itself imposes no
    /// additional checks because the controller-level gate is already the policy
    /// boundary for the whole admin surface.
    /// </remarks>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the (possibly empty) job-state list; 400 ProblemDetails on inspector failure.</returns>
    [HttpGet("jobs/state")]
    public async Task<ActionResult<IReadOnlyList<JobStateDto>>> ListJobStatesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _inspector.ListAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(result.ErrorMessage, statusCode: StatusForCode(result.ErrorCode));
    }

    /// <summary>
    /// Encodes the optional parameter dictionary as a JSON object. Null / empty maps are
    /// serialised as <c>{}</c> rather than the JSON literal <c>null</c> so the service's
    /// JSON parser consumes an empty object instead of failing the element-kind check.
    /// Mirrors the equivalent helper on <see cref="ReportsController"/>.
    /// </summary>
    /// <param name="parameters">Optional parameter dictionary, may be null.</param>
    /// <returns>A JSON object literal (never null).</returns>
    private static string SerialiseParameters(IReadOnlyDictionary<string, string?>? parameters)
    {
        return parameters is null || parameters.Count == 0
            ? "{}"
            : JsonSerializer.Serialize(parameters);
    }

    /// <summary>Maps a non-generic <see cref="Result"/> failure to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 400 ProblemDetails as appropriate.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>404 NotFound, 403 Forbidden, or 400 BadRequest.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
