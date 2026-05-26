using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// UC18 — User administration REST surface. Every endpoint is gated by the
/// <see cref="AuthorizationComposition.CnasAdmin"/> policy; the underlying service also
/// re-checks the role as defense-in-depth (CLAUDE.md §5.4 — deny by default).
/// </summary>
/// <remarks>
/// Hosts both the legacy role grant / lock endpoints (back by
/// <see cref="IUserAdministrationService"/>) and the R0059 account-state-machine
/// endpoint (<c>POST /api/users/{id}/state</c>) back by
/// <see cref="IUserAccountStateService"/>.
/// </remarks>
/// <param name="svc">Underlying user-administration service.</param>
/// <param name="stateSvc">Account state-machine service (R0059 / SEC 016).</param>
/// <param name="bulkValidator">Validator for the bulk-suspend / bulk-unlock input DTO (R2263).</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/users")]
public sealed class UsersController(
    IUserAdministrationService svc,
    IUserAccountStateService stateSvc,
    IValidator<UserAccountStateBulkInputDto> bulkValidator) : ControllerBase
{
    private readonly IUserAdministrationService _svc = svc;
    private readonly IUserAccountStateService _stateSvc = stateSvc;
    private readonly IValidator<UserAccountStateBulkInputDto> _bulkValidator = bulkValidator;

    /// <summary>Paged list of active user profiles.</summary>
    /// <param name="page">1-based page number; defaults to 1.</param>
    /// <param name="pageSize">Page size (service clamps to [1, 200]); defaults to 20.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with a paged list of users; Sqid-encoded ids per CLAUDE.md RULE 3.</returns>
    [HttpGet]
    public async Task<ActionResult<PagedResult<UserListItem>>> ListAsync(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ListAsync(new PageRequest(page, pageSize), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<PagedResult<UserListItem>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Grant a role to the specified user.</summary>
    /// <param name="id">Sqid-encoded user id.</param>
    /// <param name="body">Role grant payload — carries the role code to add.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 403 / 404 / 400 on failure.</returns>
    [HttpPost("{id}/roles/grant")]
    public async Task<IActionResult> GrantRoleAsync(
        string id,
        [FromBody] GrantRoleRequest body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await _svc.GrantRoleAsync(id, body.Role, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Revoke a role from the specified user.</summary>
    /// <param name="id">Sqid-encoded user id.</param>
    /// <param name="body">Role revoke payload — carries the role code to remove.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 403 / 404 / 400 on failure.</returns>
    [HttpPost("{id}/roles/revoke")]
    public async Task<IActionResult> RevokeRoleAsync(
        string id,
        [FromBody] GrantRoleRequest body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await _svc.RevokeRoleAsync(id, body.Role, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Lock the specified user account.</summary>
    /// <param name="id">Sqid-encoded user id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 403 / 404 / 400 on failure.</returns>
    [HttpPost("{id}/lock")]
    public async Task<IActionResult> LockAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.LockAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Unlock the specified user account.</summary>
    /// <param name="id">Sqid-encoded user id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 403 / 404 / 400 on failure.</returns>
    [HttpPost("{id}/unlock")]
    public async Task<IActionResult> UnlockAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.UnlockAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0672 / TOR CF 18.08 — soft-deletes the supplied user account
    /// (<c>UserProfile.IsActive=false</c>). The flip is gated by the
    /// audit-history guard: a brand-new user whose only event is the
    /// creation (with no recorded history or audit row attributed yet) is
    /// rejected with <see cref="ErrorCodes.UserProfileNoAuditHistory"/> so
    /// no account is deactivated without leaving a trail behind.
    /// </summary>
    /// <param name="id">Sqid-encoded user id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 204 on success (or idempotent re-deactivation);
    /// 403 when the caller lacks the <c>cnas-admin</c> role;
    /// 404 when the user is not found;
    /// 409 ProblemDetails when the audit-history guard refuses
    /// (stable <see cref="ErrorCodes.UserProfileNoAuditHistory"/> code echoed
    /// in the body's <c>detail</c>).
    /// </returns>
    [HttpPost("{id}/deactivate")]
    public async Task<IActionResult> DeactivateAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.DeactivateAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Transitions the specified user's account state per the R0059 / SEC 016 state
    /// machine. The body's <c>newState</c> field is parsed against the
    /// <see cref="UserAccountState"/> enum; unknown names short-circuit to a 400
    /// without invoking the service. The service then validates the transition is
    /// permitted (Active / Suspended / Disabled / Locked allow-list) and writes a
    /// <see cref="AuditSeverity.Critical"/> audit row.
    /// </summary>
    /// <param name="id">Sqid-encoded user id.</param>
    /// <param name="body">Request body — carries the target state name and an optional reason.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 204 on success; 400 (invalid state name / invalid sqid); 403 (caller lacks admin);
    /// 404 (user not found); 409 (transition rejected by the state machine).
    /// </returns>
    [HttpPost("{id}/state")]
    [Consumes("application/json")]
    public async Task<IActionResult> ChangeStateAsync(
        [FromRoute] string id,
        [FromBody] ChangeUserStateRequest body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        // Parse the state name at the boundary so an unknown value never reaches the
        // service — the service contract only knows about the enum, not strings. The
        // explicit string-side parsing also keeps the JSON wire format stable across
        // future enum additions (callers see "Suspended" rather than the underlying 1).
        if (!Enum.TryParse<UserAccountState>(body.NewState, ignoreCase: false, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            return Problem(
                $"Unknown UserAccountState value '{body.NewState}'. " +
                "Valid values: Active, Suspended, Disabled, Locked.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _stateSvc.ChangeStateAsync(id, parsed, body.Reason, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R2263 / SEC 016 — bulk Active → Suspended transition. Validates the payload at
    /// the boundary; the service writes one Critical-severity audit row per
    /// successful per-user transition. Per-user failures (already suspended, not
    /// found, sqid decode error) are reported in the response body's
    /// <see cref="UserAccountStateBulkResultDto.Failures"/> list; the HTTP status is
    /// 200 on any service success regardless of whether some rows failed.
    /// </summary>
    /// <param name="body">Bulk input — user sqids + reason.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with a <see cref="UserAccountStateBulkResultDto"/> on service success;
    /// 400 ProblemDetails on validation failure; 403 when the caller lacks the admin
    /// role (defense-in-depth — the controller is gated by <c>[Authorize]</c>).
    /// </returns>
    [HttpPost("bulk-suspend")]
    [Consumes("application/json")]
    public async Task<ActionResult<UserAccountStateBulkResultDto>> BulkSuspendAsync(
        [FromBody] UserAccountStateBulkInputDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        var validation = await _bulkValidator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                detail: string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _stateSvc.BulkSuspendAsync(body.UserSqids, body.Reason, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<UserAccountStateBulkResultDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R2263 / SEC 016 — bulk Locked → Active transition. Symmetric to
    /// <see cref="BulkSuspendAsync"/>. See that method's XML doc for the wire
    /// semantics.
    /// </summary>
    /// <param name="body">Bulk input — user sqids + reason.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with bulk result; 400 on validation failure; 403 on auth failure.</returns>
    [HttpPost("bulk-unlock")]
    [Consumes("application/json")]
    public async Task<ActionResult<UserAccountStateBulkResultDto>> BulkUnlockAsync(
        [FromBody] UserAccountStateBulkInputDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        var validation = await _bulkValidator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                detail: string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _stateSvc.BulkUnlockAsync(body.UserSqids, body.Reason, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<UserAccountStateBulkResultDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a generic <see cref="Result{T}"/> failure to an <see cref="ActionResult{T}"/>.</summary>
    /// <typeparam name="T">The DTO type that the action would have returned on success.</typeparam>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 403 / 400 ProblemDetails as appropriate.</returns>
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
    /// <returns>404 / 403 / 400 ProblemDetails as appropriate.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Error code; null or unknown maps to 400.</param>
    /// <returns>404 NotFound, 403 Forbidden, 409 Conflict, or 400 BadRequest.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        // R0059 transition-machine rejection is a state-machine conflict, not auth.
        ErrorCodes.UserAccountStateTransitionForbidden => StatusCodes.Status409Conflict,
        // R0672 / TOR CF 18.08 — audit-history guard refusal is a policy
        // conflict (the row is intact, the call simply doesn't meet the
        // pre-flight contract).
        ErrorCodes.UserProfileNoAuditHistory => StatusCodes.Status409Conflict,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}

/// <summary>Request body for grant/revoke role operations.</summary>
/// <param name="Role">CNAS role code, e.g. <c>cnas-user</c>, <c>cnas-decider</c>, <c>cnas-admin</c>.</param>
public sealed record GrantRoleRequest(string Role);

/// <summary>
/// Request body for the R0059 account-state-machine endpoint
/// (<c>POST /api/users/{id}/state</c>).
/// </summary>
/// <param name="NewState">
/// String form of the desired <c>UserAccountState</c> enum value
/// (<c>Active</c>, <c>Suspended</c>, <c>Disabled</c>, <c>Locked</c>). Parsed
/// case-sensitively at the controller boundary; unknown values short-circuit to 400.
/// </param>
/// <param name="Reason">
/// Optional free-form reason captured on the audit row's payload. May be null when
/// the caller has no context to record. The reason MUST NOT contain PII.
/// </param>
public sealed record ChangeUserStateRequest(string NewState, string? Reason);
