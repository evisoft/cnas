using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Abac;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2271 / TOR SEC 025 — admin REST surface over the ABAC rule-set registry.
/// Restricted to the <see cref="AuthorizationComposition.CnasAdmin"/> policy
/// because ABAC rule sets are the substrate that gates other privileged
/// endpoints — administrators here can affect every <c>[AbacPolicy("…")]</c>
/// gate in the system.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST   /api/admin/abac/rule-sets</c> — create.</item>
///   <item><c>PUT    /api/admin/abac/rule-sets/{sqid}</c> — modify metadata.</item>
///   <item><c>POST   /api/admin/abac/rule-sets/{sqid}/disable</c> — soft-delete.</item>
///   <item><c>POST   /api/admin/abac/rule-sets/{sqid}/enable</c> — re-activate.</item>
///   <item><c>POST   /api/admin/abac/rule-sets/{sqid}/rules</c> — append rule.</item>
///   <item><c>PUT    /api/admin/abac/rules/{sqid}</c> — modify rule.</item>
///   <item><c>POST   /api/admin/abac/rules/{sqid}/disable</c> — soft-delete rule.</item>
///   <item><c>POST   /api/admin/abac/rules/{sqid}/enable</c> — re-activate rule.</item>
///   <item><c>POST   /api/admin/abac/rule-sets/{sqid}/rules/reorder</c> — bulk reorder.</item>
///   <item><c>GET    /api/admin/abac/rule-sets/{sqid}</c> — single fetch.</item>
///   <item><c>GET    /api/admin/abac/rule-sets/by-policy-name/{policyName}</c> — fetch by policy name.</item>
///   <item><c>GET    /api/admin/abac/rule-sets?policyName=…&amp;isActive=…&amp;skip=…&amp;take=…</c> — list.</item>
///   <item><c>POST   /api/admin/abac/test-expression</c> — dry-run evaluator.</item>
/// </list>
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/abac")]
public sealed class AbacAdminController : ControllerBase
{
    private readonly IAbacRuleRegistryService _service;

    /// <summary>Constructs the controller with its scoped collaborator.</summary>
    /// <param name="service">ABAC registry service façade.</param>
    public AbacAdminController(IAbacRuleRegistryService service)
    {
        System.ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <summary>Creates a new ABAC rule set.</summary>
    /// <param name="input">Validated create envelope.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>201 with the created DTO, or 400 / 401 / 409 on failure.</returns>
    [HttpPost("rule-sets")]
    [Consumes("application/json")]
    public async Task<ActionResult<AbacRuleSetDto>> CreateRuleSetAsync(
        [FromBody] AbacRuleSetCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CreateRuleSetAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Created($"/api/admin/abac/rule-sets/{result.Value.Id}", result.Value)
            : MapFailure<AbacRuleSetDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Modifies a rule set's metadata.</summary>
    /// <param name="sqid">Sqid-encoded rule-set id.</param>
    /// <param name="input">Validated modify envelope.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO, or 400 / 404 on failure.</returns>
    [HttpPut("rule-sets/{sqid}")]
    [Consumes("application/json")]
    public async Task<ActionResult<AbacRuleSetDto>> ModifyRuleSetAsync(
        string sqid,
        [FromBody] AbacRuleSetModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ModifyRuleSetAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure<AbacRuleSetDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Soft-deletes a rule set.</summary>
    /// <param name="sqid">Sqid-encoded rule-set id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO, or 400 / 404 on failure.</returns>
    [HttpPost("rule-sets/{sqid}/disable")]
    [Consumes("application/json")]
    public async Task<ActionResult<AbacRuleSetDto>> DisableRuleSetAsync(
        string sqid,
        [FromBody] AbacRuleReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.DisableRuleSetAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure<AbacRuleSetDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Re-activates a previously-disabled rule set.</summary>
    /// <param name="sqid">Sqid-encoded rule-set id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO, or 400 / 404 on failure.</returns>
    [HttpPost("rule-sets/{sqid}/enable")]
    [Consumes("application/json")]
    public async Task<ActionResult<AbacRuleSetDto>> EnableRuleSetAsync(
        string sqid,
        [FromBody] AbacRuleReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.EnableRuleSetAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure<AbacRuleSetDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Appends a new rule to a rule set.</summary>
    /// <param name="sqid">Sqid-encoded parent rule-set id.</param>
    /// <param name="input">Validated rule envelope.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>201 with the persisted rule DTO, or 400 / 404 on failure.</returns>
    [HttpPost("rule-sets/{sqid}/rules")]
    [Consumes("application/json")]
    public async Task<ActionResult<AbacRuleDto>> AddRuleAsync(
        string sqid,
        [FromBody] AbacRuleInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.AddRuleAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Created($"/api/admin/abac/rules/{result.Value.Id}", result.Value)
            : MapFailure<AbacRuleDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Modifies an existing rule.</summary>
    /// <param name="sqid">Sqid-encoded rule id.</param>
    /// <param name="input">Validated rule envelope.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO, or 400 / 404 on failure.</returns>
    [HttpPut("rules/{sqid}")]
    [Consumes("application/json")]
    public async Task<ActionResult<AbacRuleDto>> ModifyRuleAsync(
        string sqid,
        [FromBody] AbacRuleInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ModifyRuleAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure<AbacRuleDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Soft-deletes a rule.</summary>
    /// <param name="sqid">Sqid-encoded rule id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO, or 400 / 404 on failure.</returns>
    [HttpPost("rules/{sqid}/disable")]
    [Consumes("application/json")]
    public async Task<ActionResult<AbacRuleDto>> DisableRuleAsync(
        string sqid,
        [FromBody] AbacRuleReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.DisableRuleAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure<AbacRuleDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Re-activates a previously-disabled rule.</summary>
    /// <param name="sqid">Sqid-encoded rule id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO, or 400 / 404 on failure.</returns>
    [HttpPost("rules/{sqid}/enable")]
    [Consumes("application/json")]
    public async Task<ActionResult<AbacRuleDto>> EnableRuleAsync(
        string sqid,
        [FromBody] AbacRuleReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.EnableRuleAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure<AbacRuleDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Bulk-reorders the rules belonging to one rule set.</summary>
    /// <param name="sqid">Sqid-encoded parent rule-set id.</param>
    /// <param name="ordering">List of <c>{RuleSqid, NewOrderIndex}</c> entries.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO, or 400 / 404 on failure.</returns>
    [HttpPost("rule-sets/{sqid}/rules/reorder")]
    [Consumes("application/json")]
    public async Task<ActionResult<AbacRuleSetDto>> ReorderRulesAsync(
        string sqid,
        [FromBody] IReadOnlyList<AbacRuleReorderInputDto> ordering,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(ordering);
        var result = await _service.ReorderRulesAsync(sqid, ordering, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure<AbacRuleSetDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Fetches a single rule set by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded rule-set id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the DTO, or 400 / 404 on failure.</returns>
    [HttpGet("rule-sets/{sqid}")]
    public async Task<ActionResult<AbacRuleSetDto>> GetRuleSetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetRuleSetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure<AbacRuleSetDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Fetches a rule set by its stable policy name.</summary>
    /// <param name="policyName">SCREAMING_SNAKE_CASE policy name.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the DTO, or 404 on failure.</returns>
    [HttpGet("rule-sets/by-policy-name/{policyName}")]
    public async Task<ActionResult<AbacRuleSetDto>> GetRuleSetByPolicyNameAsync(
        string policyName,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetRuleSetByPolicyNameAsync(policyName, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure<AbacRuleSetDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists rule sets matching the supplied filter envelope.</summary>
    /// <param name="policyName">Optional policy-name filter.</param>
    /// <param name="isActive">Optional active-state filter.</param>
    /// <param name="skip">Page offset.</param>
    /// <param name="take">Page size.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the page, or 400 on validation failure.</returns>
    [HttpGet("rule-sets")]
    public async Task<ActionResult<AbacRuleSetPageDto>> ListRuleSetsAsync(
        [FromQuery] string? policyName = null,
        [FromQuery] bool? isActive = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 25,
        CancellationToken cancellationToken = default)
    {
        var filter = new AbacRuleSetFilterDto(policyName, isActive, skip, take);
        var result = await _service.ListRuleSetsAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure<AbacRuleSetPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Dry-runs the evaluator against a synthetic attribute payload.</summary>
    /// <param name="input">Validated test envelope.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the decision DTO, or 400 / 404 on failure.</returns>
    [HttpPost("test-expression")]
    [Consumes("application/json")]
    public async Task<ActionResult<AbacDecisionDto>> TestExpressionAsync(
        [FromBody] AbacExpressionTestInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.TestExpressionAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure<AbacDecisionDto>(result.ErrorCode!, result.ErrorMessage!);
    }

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
            ErrorCodes.AbacParseError => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.AbacDuplicatePolicyName => Conflict(new { error = errorCode, message = errorMessage }),
            ErrorCodes.AbacNotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.InvalidSqid => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.ValidationFailed => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Unauthorized => Unauthorized(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
