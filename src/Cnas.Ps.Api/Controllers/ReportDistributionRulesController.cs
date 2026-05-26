using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R1906 / TOR Annex 6 — admin REST surface over the per-report distribution
/// rule registry. Restricted to <see cref="AuthorizationComposition.CnasAdmin"/>
/// because distribution policies control fan-out of business reports — a
/// misconfigured rule could leak sensitive data to unauthorised recipients.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST   /api/report-distribution-rules</c> — create.</item>
///   <item><c>PUT    /api/report-distribution-rules/{sqid}</c> — modify.</item>
///   <item><c>POST   /api/report-distribution-rules/{sqid}/disable</c> — disable.</item>
///   <item><c>POST   /api/report-distribution-rules/{sqid}/enable</c> — enable.</item>
///   <item><c>DELETE /api/report-distribution-rules/{sqid}</c> — soft-delete.</item>
///   <item><c>GET    /api/report-distribution-rules/{sqid}</c> — get one.</item>
///   <item><c>GET    /api/report-distribution-rules</c> — list rules.</item>
///   <item><c>GET    /api/report-distribution-rules/dispatches</c> — list dispatches.</item>
/// </list>
/// </para>
/// </remarks>
/// <param name="service">Distribution-rule service façade.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/report-distribution-rules")]
public sealed class ReportDistributionRulesController(IReportDistributionService service) : ControllerBase
{
    private readonly IReportDistributionService _service = service;

    /// <summary>Creates a new distribution rule.</summary>
    /// <param name="input">Operator-supplied rule payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>201 with the created DTO; 400 on validation; 409 on duplicate.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<ReportDistributionRuleDto>> CreateAsync(
        [FromBody] ReportDistributionRuleCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CreateRuleAsync(input, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return CreatedAtAction(nameof(GetAsync), new { sqid = result.Value.Id }, result.Value);
        }
        return MapFailure<ReportDistributionRuleDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Modifies an existing rule.</summary>
    /// <param name="sqid">Sqid-encoded rule id.</param>
    /// <param name="input">Partial-update payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO.</returns>
    [HttpPut("{sqid}")]
    [Consumes("application/json")]
    public async Task<ActionResult<ReportDistributionRuleDto>> ModifyAsync(
        string sqid,
        [FromBody] ReportDistributionRuleModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ModifyRuleAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ReportDistributionRuleDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Disables a rule.</summary>
    /// <param name="sqid">Sqid-encoded rule id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO.</returns>
    [HttpPost("{sqid}/disable")]
    [Consumes("application/json")]
    public async Task<ActionResult<ReportDistributionRuleDto>> DisableAsync(
        string sqid,
        [FromBody] ReportDistributionReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.DisableRuleAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ReportDistributionRuleDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Re-enables a rule.</summary>
    /// <param name="sqid">Sqid-encoded rule id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO.</returns>
    [HttpPost("{sqid}/enable")]
    [Consumes("application/json")]
    public async Task<ActionResult<ReportDistributionRuleDto>> EnableAsync(
        string sqid,
        [FromBody] ReportDistributionReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.EnableRuleAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ReportDistributionRuleDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Soft-deletes a rule.</summary>
    /// <param name="sqid">Sqid-encoded rule id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 404 when missing.</returns>
    [HttpDelete("{sqid}")]
    [Consumes("application/json")]
    public async Task<IActionResult> DeleteAsync(
        string sqid,
        [FromBody] ReportDistributionReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.DeleteRuleAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return NoContent();
        }
        return result.ErrorCode switch
        {
            ErrorCodes.InvalidSqid => BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage }),
            ErrorCodes.ValidationFailed => BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = result.ErrorCode, message = result.ErrorMessage }),
            _ => StatusCode(500, new { error = result.ErrorCode, message = result.ErrorMessage }),
        };
    }

    /// <summary>Fetches one rule.</summary>
    /// <param name="sqid">Sqid-encoded rule id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 / 400 / 404.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<ReportDistributionRuleDto>> GetAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetRuleByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ReportDistributionRuleDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists rules with optional filters.</summary>
    /// <param name="reportCode">Optional exact report-code filter.</param>
    /// <param name="channel">Optional channel enum-name filter.</param>
    /// <param name="recipientKind">Optional recipient-kind enum-name filter.</param>
    /// <param name="isActive">Optional active-flag filter; null = both.</param>
    /// <param name="skip">Page offset (≥ 0).</param>
    /// <param name="take">Page size (1..200).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the page; 400 on filter failure.</returns>
    [HttpGet]
    public async Task<ActionResult<ReportDistributionRulePageDto>> ListAsync(
        [FromQuery] string? reportCode = null,
        [FromQuery] string? channel = null,
        [FromQuery] string? recipientKind = null,
        [FromQuery] bool? isActive = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new ReportDistributionRuleFilterDto(
            ReportCode: reportCode,
            Channel: channel,
            RecipientKind: recipientKind,
            IsActive: isActive,
            Skip: skip,
            Take: take);
        var result = await _service.ListRulesAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ReportDistributionRulePageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists dispatches with optional filters.</summary>
    /// <param name="reportRunSqid">Optional report-run sqid filter.</param>
    /// <param name="status">Optional status enum-name filter.</param>
    /// <param name="ruleSqid">Optional rule-sqid filter.</param>
    /// <param name="skip">Page offset (≥ 0).</param>
    /// <param name="take">Page size (1..200).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the page; 400 on filter failure.</returns>
    [HttpGet("dispatches")]
    public async Task<ActionResult<ReportDispatchPageDto>> ListDispatchesAsync(
        [FromQuery] string? reportRunSqid = null,
        [FromQuery] string? status = null,
        [FromQuery] string? ruleSqid = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new ReportDispatchFilterDto(
            ReportRunSqid: reportRunSqid,
            Status: status,
            RuleSqid: ruleSqid,
            Skip: skip,
            Take: take);
        var result = await _service.ListDispatchesAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ReportDispatchPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Translates a failed <see cref="Result{T}"/> into the appropriate
    /// <see cref="ActionResult"/>: <c>INVALID_SQID</c> /
    /// <c>VALIDATION_FAILED</c> → 400, <c>NOT_FOUND</c> → 404,
    /// <c>CONFLICT</c> → 409, anything else → 500.
    /// </summary>
    /// <typeparam name="T">DTO type that would have been returned on success.</typeparam>
    /// <param name="errorCode">Stable error code.</param>
    /// <param name="errorMessage">Human-readable description.</param>
    /// <returns>An <see cref="ActionResult{T}"/> with the right HTTP status.</returns>
    private ActionResult<T> MapFailure<T>(string errorCode, string errorMessage)
        => errorCode switch
        {
            ErrorCodes.InvalidSqid => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.ValidationFailed => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
