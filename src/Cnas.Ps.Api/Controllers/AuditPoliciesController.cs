using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0182 / SEC 042 — admin REST surface over the audit-policy registry. Restricted to
/// the <see cref="AuthorizationComposition.CnasTechAdmin"/> policy because mutating
/// audit policy is itself a security-sensitive operation (an attacker who could
/// suppress audit rows could cover their tracks).
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>GET    /api/audit-policies</c>             — list every active policy ordered by priority.</item>
///   <item><c>GET    /api/audit-policies/by-code/{code}</c> — fetch one row by natural key.</item>
///   <item><c>POST   /api/audit-policies</c>             — create a new policy.</item>
///   <item><c>PUT    /api/audit-policies/{sqid}</c>      — update an existing policy.</item>
///   <item><c>DELETE /api/audit-policies/{sqid}</c>      — soft-disable a policy.</item>
/// </list>
/// </para>
/// <para>
/// <b>Sqid convention.</b> The <c>{sqid}</c> route segment is a Sqid-encoded id per
/// CLAUDE.md RULE 3. <c>{code}</c> remains the raw natural-key string because operator
/// runbooks reference policies by code.
/// </para>
/// </remarks>
/// <param name="svc">Underlying audit-policy service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasTechAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/audit-policies")]
public sealed class AuditPoliciesController(IAuditPolicyService svc) : ControllerBase
{
    private readonly IAuditPolicyService _svc = svc;

    /// <summary>
    /// Lists every active audit policy ordered by priority ascending then code
    /// ascending. Disabled (soft-deleted) policies are excluded.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the list on success; 401 when the caller is anonymous.</returns>
    [HttpGet]
    public async Task<IActionResult> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await _svc.ListAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Fetches a single audit policy by its natural-key <c>Code</c>.
    /// </summary>
    /// <param name="code">Natural-key code (e.g. <c>solicitant.view.search</c>).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the row; 404 when the code is unknown.</returns>
    [HttpGet("by-code/{code}")]
    public async Task<IActionResult> GetByCodeAsync(
        [FromRoute] string code,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.GetByCodeAsync(code, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Persists a new audit policy. The <c>Code</c> must be globally unique — a
    /// duplicate code surfaces as 409 Conflict.
    /// </summary>
    /// <param name="input">Create payload (required body).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>201 with the Sqid id; 400 on validation; 409 on duplicate code.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<IActionResult> CreateAsync(
        [FromBody] AuditPolicyCreateInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _svc.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return MapFailure(result.ErrorCode, result.ErrorMessage);
        }
        var sqid = result.Value;
        return CreatedAtAction(nameof(GetByCodeAsync), new { code = input.Code }, sqid);
    }

    /// <summary>
    /// Updates an existing audit policy. <c>Code</c> is immutable — to change it,
    /// disable the row and create a new one.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id.</param>
    /// <param name="input">Update payload (required body).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 400/404 on failure.</returns>
    [HttpPut("{sqid}")]
    [Consumes("application/json")]
    public async Task<IActionResult> UpdateAsync(
        [FromRoute] string sqid,
        [FromBody] AuditPolicyUpdateInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _svc.UpdateAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Soft-disables the policy (flips both <c>IsEnabled</c> and <c>IsActive</c> to
    /// false). The row remains queryable for audit forensics in R0193.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 404 when the id is unknown.</returns>
    [HttpDelete("{sqid}")]
    public async Task<IActionResult> DisableAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.DisableAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a service-layer failure code to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>The mapped ProblemDetails / NotFound action result.</returns>
    private IActionResult MapFailure(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>Mapped HTTP status code.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.Conflict => StatusCodes.Status409Conflict,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
