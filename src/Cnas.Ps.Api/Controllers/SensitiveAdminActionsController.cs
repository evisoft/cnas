using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.SensitiveActions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2273 / TOR SEC 027 — admin REST surface over the generic 4-eyes admin substrate.
/// Restricted to the <see cref="AuthorizationComposition.CnasAdmin"/> policy because
/// sensitive admin actions are high-privilege operations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST /api/admin/sensitive-actions</c> — open a request (server-fills requester id from auth context).</item>
///   <item><c>POST /api/admin/sensitive-actions/{sqid}/approve</c> — approve as second operator.</item>
///   <item><c>POST /api/admin/sensitive-actions/{sqid}/reject</c> — reject as second operator.</item>
///   <item><c>POST /api/admin/sensitive-actions/{sqid}/cancel</c> — original requester (or admin) cancels.</item>
///   <item><c>GET  /api/admin/sensitive-actions/{sqid}</c> — fetch a single request.</item>
///   <item><c>GET  /api/admin/sensitive-actions?status=…&amp;actionCode=…&amp;skip=…&amp;take=…</c> — list requests.</item>
///   <item><c>GET  /api/admin/sensitive-actions/registry</c> — enumerate registered policies.</item>
/// </list>
/// </para>
/// <para>
/// <b>Server-fill of requester id.</b> The controller does NOT accept a
/// <c>RequestedByUserId</c> field from the client — the service layer reads the caller
/// from the authenticated <c>ICallerContext</c>. This is a CLAUDE.md RULE-3 boundary
/// invariant: client-supplied identity is never trusted.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/sensitive-actions")]
public sealed class SensitiveAdminActionsController : ControllerBase
{
    private readonly ISensitiveAdminActionService _service;
    private readonly ISensitiveActionRegistry _registry;

    /// <summary>Constructs the controller with its scoped collaborators.</summary>
    /// <param name="service">Generic 4-eyes substrate service.</param>
    /// <param name="registry">Read-only registry of registered policies.</param>
    public SensitiveAdminActionsController(
        ISensitiveAdminActionService service,
        ISensitiveActionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(registry);
        _service = service;
        _registry = registry;
    }

    /// <summary>
    /// Opens a new sensitive-admin-action request. The requester id is taken from the
    /// authenticated caller — the client cannot override it.
    /// </summary>
    /// <param name="input">Request envelope (ActionCode + RequestReason + RequestPayloadJson).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>201 with the created DTO, or 400/401/409 on failure.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<SensitiveAdminActionDto>> RequestAsync(
        [FromBody] SensitiveAdminActionRequestInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.RequestAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Created($"/api/admin/sensitive-actions/{result.Value.Id}", result.Value)
            : MapFailure<SensitiveAdminActionDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Approves a pending request as the second distinct operator.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="input">Approval envelope (mandatory note).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO, or 400/401/404/409 on failure.</returns>
    [HttpPost("{sqid}/approve")]
    [Consumes("application/json")]
    public async Task<ActionResult<SensitiveAdminActionDto>> ApproveAsync(
        string sqid,
        [FromBody] SensitiveAdminActionApprovalInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ApproveAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SensitiveAdminActionDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Rejects a pending request as the second distinct operator.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO, or 400/401/404/409 on failure.</returns>
    [HttpPost("{sqid}/reject")]
    [Consumes("application/json")]
    public async Task<ActionResult<SensitiveAdminActionDto>> RejectAsync(
        string sqid,
        [FromBody] SensitiveAdminActionReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.RejectAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SensitiveAdminActionDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Cancels a pending request.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO, or 400/401/404/409 on failure.</returns>
    [HttpPost("{sqid}/cancel")]
    [Consumes("application/json")]
    public async Task<ActionResult<SensitiveAdminActionDto>> CancelAsync(
        string sqid,
        [FromBody] SensitiveAdminActionReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CancelAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SensitiveAdminActionDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Fetches a single request by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 / 400 / 404.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<SensitiveAdminActionDto>> GetAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SensitiveAdminActionDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists requests filtered by status / action code / requested-at window.</summary>
    /// <param name="status">Optional <c>SensitiveAdminActionStatus</c> enum name.</param>
    /// <param name="actionCode">Optional action-code filter.</param>
    /// <param name="requestedAfter">Optional UTC lower bound on RequestedAt.</param>
    /// <param name="requestedBefore">Optional UTC upper bound on RequestedAt.</param>
    /// <param name="skip">Page offset (≥ 0; default 0).</param>
    /// <param name="take">Page size (1..100; default 25).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the paged response, or 400 on validation failure.</returns>
    [HttpGet]
    public async Task<ActionResult<SensitiveAdminActionPageDto>> ListAsync(
        [FromQuery] string? status = null,
        [FromQuery] string? actionCode = null,
        [FromQuery] DateTime? requestedAfter = null,
        [FromQuery] DateTime? requestedBefore = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 25,
        CancellationToken cancellationToken = default)
    {
        var filter = new SensitiveAdminActionFilterDto(
            Status: status,
            ActionCode: actionCode,
            RequestedAfter: requestedAfter,
            RequestedBefore: requestedBefore,
            Skip: skip,
            Take: take);
        var result = await _service.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<SensitiveAdminActionPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Enumerates every registered policy as a descriptor row.</summary>
    /// <returns>200 with the registry rows; never fails.</returns>
    [HttpGet("registry")]
    public ActionResult<IReadOnlyCollection<SensitiveActionRegistryEntryDto>> GetRegistry()
        => Ok(_registry.Describe());

    /// <summary>
    /// Translates a failed <see cref="Result{T}"/> into the appropriate
    /// <see cref="ActionResult"/>.
    /// </summary>
    /// <typeparam name="T">DTO type that would have been returned on success.</typeparam>
    /// <param name="errorCode">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="errorMessage">Human-readable description.</param>
    /// <returns>An <see cref="ActionResult{T}"/> carrying the appropriate HTTP status.</returns>
    private ActionResult<T> MapFailure<T>(string errorCode, string errorMessage)
        => errorCode switch
        {
            ErrorCodes.InvalidSqid => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.ValidationFailed => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.FourEyesUnknownAction => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Unauthorized => Unauthorized(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.FourEyesAlreadyDecided => Conflict(new { error = errorCode, message = errorMessage }),
            ErrorCodes.FourEyesSameOperator => Conflict(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
