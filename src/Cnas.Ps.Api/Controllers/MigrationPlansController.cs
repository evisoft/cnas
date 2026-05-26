using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Migration;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2430 / TOR M4 — admin REST surface over the migration-plan registry.
/// Restricted to the <see cref="AuthorizationComposition.CnasAdmin"/>
/// policy because the surface drives data-migration scheduling.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST   /api/admin/migration/plans</c> — create.</item>
///   <item><c>PUT    /api/admin/migration/plans/{sqid}</c> — modify (Draft only).</item>
///   <item><c>POST   /api/admin/migration/plans/{sqid}/submit-for-approval</c> — submit.</item>
///   <item><c>POST   /api/admin/migration/plans/{sqid}/approve</c> — approve (Draft → Approved).</item>
///   <item><c>POST   /api/admin/migration/plans/{sqid}/activate</c> — activate.</item>
///   <item><c>POST   /api/admin/migration/plans/{sqid}/suspend</c> — suspend.</item>
///   <item><c>POST   /api/admin/migration/plans/{sqid}/archive</c> — archive.</item>
///   <item><c>GET    /api/admin/migration/plans/{sqid}</c> — get by sqid.</item>
///   <item><c>GET    /api/admin/migration/plans/by-code/{code}</c> — get by stable code.</item>
///   <item><c>GET    /api/admin/migration/plans?status=…&amp;skip=…&amp;take=…</c> — list.</item>
/// </list>
/// </para>
/// </remarks>
/// <param name="service">Migration-plan service façade.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/migration/plans")]
public sealed class MigrationPlansController(IMigrationPlanService service) : ControllerBase
{
    private readonly IMigrationPlanService _service = service;

    /// <summary>Creates a new migration plan in Draft.</summary>
    /// <param name="input">Plan-creation payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the created plan, or 400 on validation failure.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<MigrationPlanDto>> CreateAsync(
        [FromBody] MigrationPlanCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MigrationPlanDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Modifies an existing Draft plan.</summary>
    /// <param name="sqid">Sqid-encoded plan id.</param>
    /// <param name="input">Modify payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated plan; 400 / 404 / 409 on failure.</returns>
    [HttpPut("{sqid}")]
    [Consumes("application/json")]
    public async Task<ActionResult<MigrationPlanDto>> ModifyAsync(
        string sqid,
        [FromBody] MigrationPlanModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ModifyAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MigrationPlanDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Submits a Draft plan for second-admin approval.</summary>
    /// <param name="sqid">Sqid-encoded plan id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the plan DTO.</returns>
    [HttpPost("{sqid}/submit-for-approval")]
    public async Task<ActionResult<MigrationPlanDto>> SubmitForApprovalAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.SubmitForApprovalAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MigrationPlanDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Approves a Draft plan.</summary>
    /// <param name="sqid">Sqid-encoded plan id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the plan DTO.</returns>
    [HttpPost("{sqid}/approve")]
    public async Task<ActionResult<MigrationPlanDto>> ApproveAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ApproveAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MigrationPlanDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Activates an Approved or Suspended plan.</summary>
    /// <param name="sqid">Sqid-encoded plan id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the plan DTO.</returns>
    [HttpPost("{sqid}/activate")]
    public async Task<ActionResult<MigrationPlanDto>> ActivateAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ActivateAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MigrationPlanDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Suspends an Active plan.</summary>
    /// <param name="sqid">Sqid-encoded plan id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the plan DTO.</returns>
    [HttpPost("{sqid}/suspend")]
    [Consumes("application/json")]
    public async Task<ActionResult<MigrationPlanDto>> SuspendAsync(
        string sqid,
        [FromBody] MigrationPlanReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.SuspendAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MigrationPlanDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Archives a plan.</summary>
    /// <param name="sqid">Sqid-encoded plan id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the plan DTO.</returns>
    [HttpPost("{sqid}/archive")]
    [Consumes("application/json")]
    public async Task<ActionResult<MigrationPlanDto>> ArchiveAsync(
        string sqid,
        [FromBody] MigrationPlanReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ArchiveAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MigrationPlanDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets a plan by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded plan id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the plan; 400 / 404 on failure.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<MigrationPlanDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MigrationPlanDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets a plan by its stable code.</summary>
    /// <param name="code">Plan code (SCREAMING_SNAKE_CASE).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the plan; 400 / 404 on failure.</returns>
    [HttpGet("by-code/{code}")]
    public async Task<ActionResult<MigrationPlanDto>> GetByCodeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByCodeAsync(code, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MigrationPlanDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists plans matching the filter.</summary>
    /// <param name="status">Optional status filter (stable enum-name).</param>
    /// <param name="targetEntityName">Optional target-entity-name filter.</param>
    /// <param name="skip">Page offset (≥ 0).</param>
    /// <param name="take">Page size (1..100).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the page; 400 on validation failure.</returns>
    [HttpGet]
    public async Task<ActionResult<MigrationPlanPageDto>> ListAsync(
        [FromQuery] string? status = null,
        [FromQuery] string? targetEntityName = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new MigrationPlanFilterDto(status, targetEntityName, skip, take);
        var result = await _service.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MigrationPlanPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Translates a failed <see cref="Result{T}"/> into the appropriate
    /// <see cref="ActionResult"/>: invalid-sqid / validation → 400,
    /// not-found → 404, conflict → 409, anything else → 500.
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
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            IMigrationPlanService.DuplicatePlanCodeCode => Conflict(new { error = errorCode, message = errorMessage }),
            IMigrationPlanService.InvalidTransitionCode => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
