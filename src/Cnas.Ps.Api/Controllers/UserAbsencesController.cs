using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.WorkflowTasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0127 / CF 16.11 — admin REST surface over <see cref="IUserAbsenceService"/>. Allows
/// planning, fetching, listing, and cancelling user-absence rows; activation and
/// completion are driven by the <c>UserAbsenceLifecycleJob</c> rather than exposed via
/// HTTP.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorization.</b> Restricted to the <see cref="AuthorizationComposition.CnasAdmin"/>
/// policy — operator-level visibility is required to nominate a delegate for another
/// user's tasks. Service-level role checks remain in place as defense-in-depth.
/// </para>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST   /api/user-absences</c>                 — plan a new absence.</item>
///   <item><c>GET    /api/user-absences/{sqid}</c>          — fetch one row.</item>
///   <item><c>POST   /api/user-absences/{sqid}/cancel</c>   — cancel a Planned row.</item>
/// </list>
/// Per-user listing lives on <see cref="UsersAbsencesByUserController"/> — bound to the
/// canonical <c>/api/users/{userSqid}/absences</c> route per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
/// <param name="svc">Underlying user-absence service.</param>
/// <param name="sqids">Sqid encoder/decoder for route + body sqids.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/user-absences")]
public sealed class UserAbsencesController(IUserAbsenceService svc, ISqidService sqids) : ControllerBase
{
    private readonly IUserAbsenceService _svc = svc;
    private readonly ISqidService _sqids = sqids;

    /// <summary>
    /// Plans a new absence. The service validates the payload, decodes the user +
    /// delegate Sqids, and persists a <c>Planned</c> row.
    /// </summary>
    /// <param name="body">Plan payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 201 Created with the persisted DTO and a <c>Location</c> header; 400 on
    /// validation failures; 404 when either user is unknown.
    /// </returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<UserAbsenceOutputDto>> PlanAsync(
        [FromBody] UserAbsenceCreateDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        var result = await _svc.PlanAsync(body, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return MapFailureGeneric<UserAbsenceOutputDto>(result.ErrorCode, result.ErrorMessage);
        }

        // CreatedAtAction would couple us to the action name; CreatedResult with the
        // canonical route + the Sqid id is sufficient for a Location header.
        return Created($"/api/user-absences/{result.Value!.Id}", result.Value);
    }

    /// <summary>
    /// Fetches a single absence by Sqid.
    /// </summary>
    /// <param name="sqid">Sqid-encoded absence id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the row; 400 on malformed Sqid; 404 when missing.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<UserAbsenceOutputDto>> GetAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }

        var dto = await _svc.GetAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>
    /// Cancels a Planned absence. Active rows must be completed instead so the revert
    /// sweep runs — see <see cref="IUserAbsenceService.CancelAsync"/>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded absence id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 400 on malformed Sqid / invalid state; 404 when missing.</returns>
    [HttpPost("{sqid}/cancel")]
    public async Task<IActionResult> CancelAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _svc.CancelAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? NoContent()
            : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Maps a non-generic <see cref="Result"/> failure to an <see cref="IActionResult"/>.
    /// Mirrors the helper in <see cref="TasksController"/> so the two controllers share
    /// the same error-code → HTTP-status policy.
    /// </summary>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>; may be null.</param>
    /// <param name="message">Human-readable detail forwarded into ProblemDetails.</param>
    /// <returns>404 for <see cref="ErrorCodes.NotFound"/>; otherwise ProblemDetails at the mapped status.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>
    /// Generic-arity twin of <see cref="MapFailureBare"/> for actions returning
    /// <c>ActionResult&lt;T&gt;</c>.
    /// </summary>
    /// <typeparam name="T">Type-parameter of the action's <c>ActionResult&lt;T&gt;</c>.</typeparam>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / ProblemDetails per <see cref="StatusForCode"/>.</returns>
    private ActionResult<T> MapFailureGeneric<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>The mapped HTTP status code.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}

/// <summary>
/// R0127 / CF 16.11 — per-user listing of absence rows. Lives on the canonical
/// <c>/api/users/{userSqid}/absences</c> path because the list is a sub-resource of the
/// user — separating from <see cref="UserAbsencesController"/> keeps the by-user route
/// table clean and avoids overloading the absence id parameter with two meanings.
/// </summary>
/// <param name="svc">Underlying user-absence service.</param>
/// <param name="sqids">Sqid encoder/decoder.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/users/{userSqid}/absences")]
public sealed class UsersAbsencesByUserController(IUserAbsenceService svc, ISqidService sqids)
    : ControllerBase
{
    private readonly IUserAbsenceService _svc = svc;
    private readonly ISqidService _sqids = sqids;

    /// <summary>Lists every absence row for the supplied user, newest start first.</summary>
    /// <param name="userSqid">Sqid-encoded user id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the list; 400 on malformed Sqid.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserAbsenceOutputDto>>> ListAsync(
        [FromRoute] string userSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(userSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }
        var rows = await _svc.ListForUserAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
        return Ok(rows);
    }
}
