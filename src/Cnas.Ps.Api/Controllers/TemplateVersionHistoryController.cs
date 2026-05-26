using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Templates;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0132 / CF 17.18 — admin REST surface over the template-version retrieval +
/// diff + rollback operations. Versions are addressed by their Sqid-encoded surrogate
/// id (CLAUDE.md RULE 3); template codes are stable kebab-case strings and travel as
/// path segments without Sqid encoding (mirroring <c>WorkflowsController</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>GET  /api/admin/templates/{code}/versions</c>                   — paged list of all versions.</item>
///   <item><c>GET  /api/admin/templates/versions/{baseline}/diff/{current}</c> — structured diff.</item>
///   <item><c>POST /api/admin/templates/versions/{target}/rollback</c>         — rollback to target.</item>
/// </list>
/// </para>
/// </remarks>
/// <param name="history">Underlying history service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/templates")]
public sealed class TemplateVersionHistoryController(ITemplateVersionHistoryService history)
    : ControllerBase
{
    private readonly ITemplateVersionHistoryService _history = history;

    /// <summary>
    /// Lists historical versions for the template identified by <paramref name="code"/>.
    /// Ordered Version DESC so the current version is first.
    /// </summary>
    /// <param name="code">Stable kebab-case template code.</param>
    /// <param name="skip">Zero-based offset.</param>
    /// <param name="take">Page size (1..200).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the page; 400 / 404 on failure.</returns>
    [HttpGet("{code}/versions")]
    public async Task<IActionResult> ListVersionsAsync(
        [FromRoute] string code,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await _history.ListVersionsAsync(code, skip, take, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Diffs two versions of the same template. Cross-template diffs are rejected with
    /// <see cref="ErrorCodes.TemplateVersionMismatch"/>.
    /// </summary>
    /// <param name="baseline">Sqid-encoded id of the baseline (earlier) version.</param>
    /// <param name="current">Sqid-encoded id of the current (later) version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the diff; 400 / 404 on failure.</returns>
    [HttpGet("versions/{baseline}/diff/{current}")]
    public async Task<IActionResult> DiffAsync(
        [FromRoute] string baseline,
        [FromRoute] string current,
        CancellationToken cancellationToken = default)
    {
        var result = await _history.DiffAsync(baseline, current, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Rolls a template back to <paramref name="target"/> by minting a NEW current version
    /// that copies the target row's content. The original target row is preserved verbatim.
    /// </summary>
    /// <param name="target">Sqid-encoded id of the older version to roll back to.</param>
    /// <param name="input">Rollback metadata (required reason).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the new current version; 400 / 404 on failure.</returns>
    [HttpPost("versions/{target}/rollback")]
    [Consumes("application/json")]
    public async Task<IActionResult> RollbackAsync(
        [FromRoute] string target,
        [FromBody] TemplateRollbackInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _history.RollbackToAsync(target, input, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a service-layer failure code to an HTTP response.</summary>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Detail message.</param>
    /// <returns>404 / 400 ProblemDetails as appropriate.</returns>
    private IActionResult MapFailure(string? code, string? message) => code switch
    {
        ErrorCodes.NotFound => NotFound(),
        ErrorCodes.Forbidden => Forbid(),
        ErrorCodes.Unauthorized => Unauthorized(),
        ErrorCodes.Conflict => Conflict(message),
        _ => Problem(message, statusCode: StatusCodes.Status400BadRequest),
    };
}
