using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// UC06 / UC21 — Applications (Cereri) REST surface. Auth required for every endpoint;
/// per-action policy refinements are applied via <c>[Authorize(Policy = ...)]</c> on the
/// specific action.
/// </summary>
/// <remarks>
/// <para>
/// <b>Action policy table.</b>
/// <list type="bullet">
///   <item><c>POST   /api/applications</c>                — any authenticated caller (citizen submission).</item>
///   <item><c>GET    /api/applications/mine</c>           — any authenticated caller (own submissions).</item>
///   <item><c>GET    /api/applications/{id}</c>           — any authenticated caller (own submissions; service-layer ownership check).</item>
///   <item><c>POST   /api/applications/{id}/withdraw</c>  — any authenticated caller (own submissions; service-layer ownership check).</item>
///   <item><c>POST   /api/applications/{id}/advance</c>   — <see cref="AuthorizationComposition.CnasDecider"/> (system-actor automation surface, UC21).</item>
/// </list>
/// </para>
/// <para>
/// <b>BUG-021 fix.</b> Previously, every service-layer failure on
/// <see cref="WithdrawAsync"/> collapsed to <c>Problem(statusCode: 400)</c> — three
/// distinct failure modes (Forbidden, NotFound, ApplicationLocked) surfaced identically.
/// This controller now uses the same <c>StatusForCode</c> translation helper as
/// <see cref="AdminController"/> / <see cref="UsersController"/> so the HTTP status
/// reflects the underlying error code.
/// </para>
/// </remarks>
/// <param name="applications">Underlying citizen-application service (UC06).</param>
/// <param name="processing">Underlying system-actor processing service (UC21).</param>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/applications")]
public sealed class ApplicationsController(
    IApplicationService applications,
    IApplicationProcessingService processing) : ControllerBase
{
    private readonly IApplicationService _applications = applications;
    private readonly IApplicationProcessingService _processing = processing;

    /// <summary>Submit a new application.</summary>
    /// <param name="input">Service-passport reference + form payload + attachment ids.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>201 Created on success; 400 / 403 / 404 ProblemDetails on failure.</returns>
    [HttpPost]
    public async Task<ActionResult<ApplicationOutput>> SubmitAsync(
        [FromBody] SubmitApplicationInput input,
        CancellationToken cancellationToken = default)
    {
        var result = await _applications.SubmitAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetAsync), new { id = result.Value.Id }, result.Value)
            : MapFailureGeneric<ApplicationOutput>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>List the caller's applications.</summary>
    /// <param name="page">1-based page number; defaults to 1.</param>
    /// <param name="pageSize">Items per page; defaults to 20.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with a paged list of the caller's applications.</returns>
    [HttpGet("mine")]
    public async Task<ActionResult<PagedResult<ApplicationListItemOutput>>> MineAsync(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _applications.MineAsync(new PageRequest(page, pageSize), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<PagedResult<ApplicationListItemOutput>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Get a single application by Sqid id.</summary>
    /// <param name="id">Sqid-encoded application id (CLAUDE.md RULE 3).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the application; 404 when not found / not owned.</returns>
    [HttpGet("{id}")]
    public async Task<ActionResult<ApplicationOutput>> GetAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var result = await _applications.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : NotFound();
    }

    /// <summary>
    /// Withdraw an in-flight application. The service-layer ownership check enforces that
    /// only the solicitant can withdraw their own application; foreign callers receive
    /// <see cref="ErrorCodes.Forbidden"/> which this controller maps to HTTP 403.
    /// </summary>
    /// <remarks>
    /// <b>BUG-021 fix.</b> Replaces the prior uniform <c>Problem(statusCode: 400)</c>
    /// mapping with a code-aware translation. The three failure modes
    /// (<see cref="ErrorCodes.NotFound"/>, <see cref="ErrorCodes.Forbidden"/>,
    /// <see cref="ErrorCodes.ApplicationLocked"/>) now produce 404, 403, and 409
    /// respectively — see <see cref="StatusForCode"/>.
    /// </remarks>
    /// <param name="id">Sqid-encoded application id (CLAUDE.md RULE 3).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 403 / 404 / 409 / 400 on failure.</returns>
    [HttpPost("{id}/withdraw")]
    public async Task<IActionResult> WithdrawAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var result = await _applications.WithdrawAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// UC21 — System-actor advance. Drives a <c>Submitted</c> application through its
    /// intake → examination (or auto-reject) transition by evaluating the passport's
    /// decision rules and either opening a dossier or rejecting the application. The
    /// underlying service handles the dossier creation, audit journaling, notification
    /// dispatch, and MCabinet projection.
    /// </summary>
    /// <remarks>
    /// <b>Authorization choice.</b> The endpoint is gated by
    /// <see cref="AuthorizationComposition.CnasDecider"/> because UC21's "system actor"
    /// in practice runs as an authenticated decider account from the automation pool. A
    /// pure-system policy would require a separate machine-credential pipeline (out of
    /// scope for this batch); using <c>CnasDecider</c> is the closest existing policy
    /// that grants the necessary privilege without inventing a new role string. Higher
    /// roles (<see cref="AuthorizationComposition.CnasAdmin"/>) satisfy this policy
    /// transparently per the policy ladder.
    /// </remarks>
    /// <param name="id">Sqid-encoded application id (CLAUDE.md RULE 3).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 204 No Content on success (whether the application was accepted into examination
    /// or auto-rejected — both are "successful advances" per the service contract);
    /// 404 when the application or its passport is missing; 409 when the application is
    /// not in <see cref="ErrorCodes.ApplicationNotSubmitted"/> state; 400 / 403 otherwise.
    /// </returns>
    [HttpPost("{id}/advance")]
    [Authorize(Policy = AuthorizationComposition.CnasDecider)]
    public async Task<IActionResult> AdvanceAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var result = await _processing.AdvanceAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a generic <see cref="Result{T}"/> failure to an <see cref="ActionResult{T}"/>.</summary>
    /// <typeparam name="T">The DTO type that the action would have returned on success.</typeparam>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 403 / 409 / 400 ProblemDetails as appropriate.</returns>
    private ActionResult<T> MapFailureGeneric<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Maps a non-generic <see cref="Result"/> failure to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 403 / 409 / 400 ProblemDetails as appropriate.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <remarks>
    /// <b>BUG-021 mapping.</b> Replaces the prior uniform-400 surface with a code-aware
    /// table:
    /// <list type="bullet">
    ///   <item><see cref="ErrorCodes.NotFound"/> → 404 NotFound.</item>
    ///   <item><see cref="ErrorCodes.Forbidden"/> → 403 Forbidden (the ownership-check
    ///         failure mode that was previously masked as 400).</item>
    ///   <item><see cref="ErrorCodes.ApplicationLocked"/> → 409 Conflict (final-state
    ///         application that cannot be re-transitioned).</item>
    ///   <item><see cref="ErrorCodes.ApplicationNotSubmitted"/> → 409 Conflict (advance
    ///         from a non-Submitted state).</item>
    ///   <item><see cref="ErrorCodes.InvalidSqid"/> /
    ///         <see cref="ErrorCodes.ValidationFailed"/> → 400 BadRequest.</item>
    ///   <item>Anything else → 400 BadRequest (defensive default).</item>
    /// </list>
    /// </remarks>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>HTTP status code.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.ApplicationLocked => StatusCodes.Status409Conflict,
        ErrorCodes.ApplicationNotSubmitted => StatusCodes.Status409Conflict,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
