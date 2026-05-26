using Cnas.Ps.Application.ContributorProfileUpdates;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0363 / TOR UC13 strategy 3 — REST surface for the on-demand external-data refresh
/// of a contributor's profile. All routes require the <c>cnas-admin</c> or
/// <c>cnas-user</c> role (the <c>Contributor.RefreshFromExternal</c> permission is the
/// stable code name; the role mapping reflects current claim wiring).
/// </summary>
/// <param name="svc">Underlying profile-refresh service.</param>
/// <param name="sqids">Sqid encoder/decoder.</param>
[ApiController]
[Authorize(Roles = "cnas-user,cnas-admin")]
public sealed class ContributorProfileRefreshController(
    IProfileRefreshService svc,
    ISqidService sqids) : ControllerBase
{
    private readonly IProfileRefreshService _svc = svc;
    private readonly ISqidService _sqids = sqids;

    /// <summary>
    /// Triggers an external-data refresh against <paramref name="source"/> for the
    /// supplied contributor. The deltas returned by the gateway are applied via the
    /// matching contributor-side writer; the run row is persisted regardless of the
    /// outcome.
    /// </summary>
    /// <param name="contributorSqid">Sqid-encoded <c>InsuredPerson</c> id.</param>
    /// <param name="source">Source code: <c>RSP</c> / <c>RSUD</c> / <c>SI_SFS</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("api/contributors/{contributorSqid}/refresh-from-source")]
    public async Task<ActionResult<ProfileRefreshRunDto>> RefreshAsync(
        [FromRoute] string contributorSqid,
        [FromQuery] string source,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return Problem("source query parameter is required.", statusCode: StatusCodes.Status400BadRequest);
        }
        var decoded = _sqids.TryDecode(contributorSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }
        var result = await _svc.RefreshFromSourceAsync(source, decoded.Value, ct).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure<ProfileRefreshRunDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Lists the most recent refresh runs, newest first.</summary>
    /// <param name="take">Maximum rows to return (clamped to <c>[1, 500]</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("api/contributor-profile-refresh-runs")]
    public async Task<ActionResult<IReadOnlyList<ProfileRefreshRunDto>>> ListRecentAsync(
        [FromQuery] int take = 50, CancellationToken ct = default)
    {
        var result = await _svc.ListRecentAsync(take, ct).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure<IReadOnlyList<ProfileRefreshRunDto>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a stable error code to an HTTP status.</summary>
    /// <typeparam name="T">DTO type.</typeparam>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Detail.</param>
    private ActionResult<T> MapFailure<T>(string? code, string? message)
    {
        var status = code switch
        {
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
            ErrorCodes.ProfileRefreshUnknownSource => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status400BadRequest,
        };
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }
}
