using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.PersonalAccount;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0516 / TOR CF 02.04 — authenticated self-service surface for the citizen
/// personal-account ("Cont personal") extract. Two endpoints:
/// <list type="bullet">
///   <item><c>GET /api/self-service/personal-account/extract</c> — the
///   caller's own account (Solicitant resolved server-side).</item>
///   <item><c>GET /api/admin/personal-account/{solicitantSqid}/extract</c> —
///   the explicit-solicitant variant, gated by the
///   <c>PersonalAccount.ReadAny</c> permission.</item>
/// </list>
/// Both endpoints emit the same wire shape so a citizen-facing UI and a
/// back-office assistance UI can share a single rendering component.
/// </summary>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
public sealed class PersonalAccountController : ControllerBase
{
    private readonly IPersonalAccountExtractService _extracts;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the controller with its service collaborators.</summary>
    /// <param name="extracts">R0516 personal-account extract service.</param>
    /// <param name="sqids">Sqid decoder for the admin route parameter.</param>
    public PersonalAccountController(
        IPersonalAccountExtractService extracts,
        ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(extracts);
        ArgumentNullException.ThrowIfNull(sqids);
        _extracts = extracts;
        _sqids = sqids;
    }

    /// <summary>
    /// R0516 — returns the personal-account extract for the calling user.
    /// </summary>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// 200 OK with the populated <see cref="PersonalAccountExtractDto"/>;
    /// 401 ProblemDetails when the caller is anonymous; 404 when no
    /// Solicitant or PersonalAccount is on file for the caller.
    /// </returns>
    [HttpGet("api/self-service/personal-account/extract")]
    public async Task<ActionResult<PersonalAccountExtractDto>> GetMineAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _extracts
            .GetForCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0516 — admin / utilizator-autorizat variant: returns the
    /// personal-account extract for the supplied Solicitant.
    /// </summary>
    /// <param name="solicitantSqid">Sqid-encoded Solicitant identifier.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// 200 OK with the populated <see cref="PersonalAccountExtractDto"/>;
    /// 400 ProblemDetails when the Sqid is malformed;
    /// 403 ProblemDetails when the caller lacks the
    /// <c>PersonalAccount.ReadAny</c> permission; 404 when no PersonalAccount
    /// is on file for the supplied Solicitant.
    /// </returns>
    [HttpGet("api/admin/personal-account/{solicitantSqid}/extract")]
    public async Task<ActionResult<PersonalAccountExtractDto>> GetForSolicitantAsync(
        string solicitantSqid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solicitantSqid);

        var decoded = _sqids.TryDecode(solicitantSqid);
        if (decoded.IsFailure)
        {
            return MapFailure(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _extracts
            .GetForSolicitantAsync(decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Maps a service-level <see cref="Result"/> failure to the appropriate
    /// ProblemDetails ActionResult. <see cref="ErrorCodes.NotFound"/> → 404;
    /// <see cref="ErrorCodes.Unauthorized"/> → 401;
    /// <see cref="ErrorCodes.Forbidden"/> → 403; everything else → 400.
    /// </summary>
    /// <param name="errorCode">Stable error code from the service.</param>
    /// <param name="errorMessage">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ObjectResult MapFailure(string? errorCode, string? errorMessage)
    {
        var status = errorCode switch
        {
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest,
        };
        var problem = new ProblemDetails
        {
            Status = status,
            Title = "Personal-account extract rejected.",
            Detail = errorMessage,
        };
        problem.Extensions["errorCode"] = errorCode;
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
