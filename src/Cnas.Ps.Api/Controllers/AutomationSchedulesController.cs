using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0200 / TOR CF 20.01-03, MR 012 — admin REST surface over
/// <see cref="ICronAdminService"/>. Lists every embedded Quartz job with its
/// effective cron expression, upserts the cron expression on a per-job basis, and
/// pauses / resumes individual jobs. Gated by
/// <see cref="AuthorizationComposition.CnasTechAdmin"/> — only technical
/// administrators can edit job cadence.
/// </summary>
/// <remarks>
/// <para>
/// Route table:
/// <list type="bullet">
///   <item><c>GET    /api/automation/schedules</c>              — list every job.</item>
///   <item><c>POST   /api/automation/schedules/{code}</c>       — upsert cron.</item>
///   <item><c>POST   /api/automation/schedules/{code}/pause</c> — pause.</item>
///   <item><c>DELETE /api/automation/schedules/{code}/pause</c> — resume.</item>
/// </list>
/// </para>
/// <para>
/// <b>Job-code semantics.</b> The <c>code</c> route segment is the stable Quartz
/// job name (e.g. <c>mpay-dispatcher</c>) — NOT a Sqid. Mirrors
/// <see cref="AutomationController"/>'s convention.
/// </para>
/// </remarks>
/// <param name="cronAdmin">Underlying admin service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasTechAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/automation/schedules")]
public sealed class AutomationSchedulesController(
    ICronAdminService cronAdmin) : ControllerBase
{
    private readonly ICronAdminService _cronAdmin = cronAdmin;

    /// <summary>
    /// Lists every Quartz job registered with the scheduler together with its
    /// currently-effective cron expression. Jobs that have no operator override yet
    /// surface with <c>Id = null</c>, <c>IsOverridden = false</c>, and the baked-in
    /// default cron mirrored into <c>CronExpression</c>.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the list; 500 ProblemDetails on inspector failure.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<JobScheduleOverrideDto>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _cronAdmin.ListAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(result.ErrorMessage, statusCode: StatusForCode(result.ErrorCode));
    }

    /// <summary>
    /// Upserts the cron expression on the named Quartz job. Creates an override row on
    /// first call; updates the existing row on subsequent calls. The change is also
    /// applied to the live scheduler so it takes effect immediately.
    /// </summary>
    /// <param name="code">Stable Quartz job code (NOT a Sqid).</param>
    /// <param name="body">Payload carrying the new cron expression.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO; 400 / 404 ProblemDetails on failure.</returns>
    [HttpPost("{code}")]
    public async Task<ActionResult<JobScheduleOverrideDto>> UpsertAsync(
        [FromRoute] string code,
        [FromBody] CronExpressionInputDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await _cronAdmin.UpsertAsync(code, body, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }
        return MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Pauses the named Quartz job. The pause is persisted (so it survives restart)
    /// and applied to the live scheduler.
    /// </summary>
    /// <param name="code">Stable Quartz job code (NOT a Sqid).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO; 404 ProblemDetails when the job is unknown.</returns>
    [HttpPost("{code}/pause")]
    public async Task<ActionResult<JobScheduleOverrideDto>> PauseAsync(
        [FromRoute] string code,
        CancellationToken cancellationToken = default)
    {
        var result = await _cronAdmin.PauseAsync(code, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }
        return MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Resumes a previously-paused Quartz job. The resume is persisted (so it survives
    /// restart) and applied to the live scheduler.
    /// </summary>
    /// <param name="code">Stable Quartz job code (NOT a Sqid).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO; 404 ProblemDetails when the job is unknown.</returns>
    [HttpDelete("{code}/pause")]
    public async Task<ActionResult<JobScheduleOverrideDto>> ResumeAsync(
        [FromRoute] string code,
        CancellationToken cancellationToken = default)
    {
        var result = await _cronAdmin.ResumeAsync(code, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }
        return MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a generic <see cref="Result{T}"/> failure to an <see cref="ActionResult"/>.</summary>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/> or <see cref="ICronAdminService"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>The matching ProblemDetails / NotFound response.</returns>
    private ActionResult MapFailure(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound(message)
            : (ActionResult)Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable error code into an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>404 / 403 / 400 / 500 as appropriate.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.Internal => StatusCodes.Status500InternalServerError,
        ICronAdminService.UnknownJobCode => StatusCodes.Status404NotFound,
        ICronAdminService.InvalidCronCode => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
