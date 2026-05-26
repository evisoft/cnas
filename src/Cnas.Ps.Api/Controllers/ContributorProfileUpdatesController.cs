using Cnas.Ps.Application.ContributorProfileUpdates;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0362 / TOR UC13 — REST surface for workflow-driven contributor-profile updates.
/// Solicitants submit a request via <c>POST /api/contributor-profile-updates</c>; an
/// administrator approves or rejects via the corresponding sub-routes. All routes are
/// authenticated; approve/reject additionally require the <c>cnas-admin</c> role
/// (defense-in-depth re-check happens inside <see cref="IProfileUpdateService"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Status code mapping.</b>
/// <list type="bullet">
///   <item><see cref="ErrorCodes.Forbidden"/> → 403.</item>
///   <item><see cref="ErrorCodes.Unauthorized"/> → 401.</item>
///   <item><see cref="ErrorCodes.NotFound"/> → 404.</item>
///   <item><see cref="ErrorCodes.Conflict"/> → 409.</item>
///   <item>Everything else → 400.</item>
/// </list>
/// </para>
/// </remarks>
/// <param name="svc">Underlying profile-update service.</param>
/// <param name="sqids">Sqid encoder/decoder.</param>
[ApiController]
[Authorize]
[Route("api/contributor-profile-updates")]
public sealed class ContributorProfileUpdatesController(
    IProfileUpdateService svc,
    ISqidService sqids) : ControllerBase
{
    private readonly IProfileUpdateService _svc = svc;
    private readonly ISqidService _sqids = sqids;

    /// <summary>Submits a new profile-update request.</summary>
    /// <param name="body">Submission payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>201 with the created DTO on success; 400/401/404 on failure.</returns>
    [HttpPost]
    public async Task<ActionResult<ProfileUpdateRequestDto>> SubmitAsync(
        [FromBody] ProfileUpdateRequestSubmitDto body,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await _svc.SubmitAsync(body, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return CreatedAtAction(nameof(GetAsync), new { sqid = result.Value.Id }, result.Value);
        }
        return MapFailure<ProfileUpdateRequestDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Loads one profile-update request by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded id of the request row.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<ProfileUpdateRequestDto>> GetAsync(
        [FromRoute] string sqid, CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }
        var result = await _svc.GetAsync(decoded.Value, ct).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure<ProfileUpdateRequestDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Approves a profile-update request and applies the change.</summary>
    /// <param name="sqid">Sqid-encoded id of the request row.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{sqid}/approve")]
    [Authorize(Roles = "cnas-admin")]
    public async Task<ActionResult<ProfileUpdateRequestDto>> ApproveAsync(
        [FromRoute] string sqid, CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }
        var result = await _svc.ApproveAsync(decoded.Value, ct).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure<ProfileUpdateRequestDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Rejects a profile-update request with a free-form reason.</summary>
    /// <param name="sqid">Sqid-encoded id of the request row.</param>
    /// <param name="body">Rejection payload.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{sqid}/reject")]
    [Authorize(Roles = "cnas-admin")]
    public async Task<IActionResult> RejectAsync(
        [FromRoute] string sqid,
        [FromBody] ProfileUpdateRejectRequest body,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }
        var result = await _svc.RejectAsync(decoded.Value, body.Reason, ct).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a generic <see cref="Result{T}"/> failure to an <see cref="ActionResult{T}"/>.</summary>
    /// <typeparam name="T">DTO type.</typeparam>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Detail.</param>
    private ActionResult<T> MapFailure<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Maps a non-generic <see cref="Result"/> failure to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Detail.</param>
    private IActionResult MapFailureBare(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Maps a stable error code to an HTTP status.</summary>
    /// <param name="code">Stable error code; null defaults to 400.</param>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    };
}
