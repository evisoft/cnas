using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Help;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0225 / TOR UI 015 — admin REST surface over the contextual-help registry.
/// Restricted to the <see cref="AuthorizationComposition.CnasAdmin"/> policy.
/// Pairs with <see cref="HelpPublicController"/> for the anonymous read endpoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>GET    /api/admin/help/topics?module=</c></item>
///   <item><c>GET    /api/admin/help/topics/{sqid}</c></item>
///   <item><c>POST   /api/admin/help/topics</c></item>
///   <item><c>PUT    /api/admin/help/topics/{sqid}</c></item>
///   <item><c>PUT    /api/admin/help/topics/{sqid}/translations/{language}</c></item>
///   <item><c>POST   /api/admin/help/translations/{sqid}/approve</c></item>
/// </list>
/// </para>
/// </remarks>
/// <param name="topics">Underlying topic service.</param>
/// <param name="translations">Underlying translation service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/help")]
public sealed class HelpAdminController(
    IHelpTopicService topics,
    IHelpTopicTranslationService translations) : ControllerBase
{
    private readonly IHelpTopicService _topics = topics;
    private readonly IHelpTopicTranslationService _translations = translations;

    /// <summary>Lists every active topic, optionally filtered by module.</summary>
    /// <param name="module">Optional module filter.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the list.</returns>
    [HttpGet("topics")]
    public async Task<IActionResult> ListTopicsAsync(
        [FromQuery] string? module = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _topics.ListAsync(module, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Fetches a single topic with every translation.</summary>
    /// <param name="sqid">Sqid-encoded id of the topic row.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the topic DTO; 404 when missing.</returns>
    [HttpGet("topics/{sqid}")]
    public async Task<IActionResult> GetTopicAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _topics.GetAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Creates a new help topic.</summary>
    /// <param name="input">Authoring payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the resulting DTO; 400 / 409 on failure.</returns>
    [HttpPost("topics")]
    [Consumes("application/json")]
    public async Task<IActionResult> CreateTopicAsync(
        [FromBody] HelpTopicUpsertDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _topics.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Updates an existing topic. The code is immutable.</summary>
    /// <param name="sqid">Sqid-encoded id of the topic row.</param>
    /// <param name="input">Authoring payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 on success; 404 when missing.</returns>
    [HttpPut("topics/{sqid}")]
    [Consumes("application/json")]
    public async Task<IActionResult> UpdateTopicAsync(
        [FromRoute] string sqid,
        [FromBody] HelpTopicUpsertDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _topics.UpdateAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Idempotent (topic, language) translation upsert.</summary>
    /// <param name="sqid">Sqid-encoded id of the parent topic.</param>
    /// <param name="language">ISO-639-1 language code.</param>
    /// <param name="input">Authoring payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the resulting translation DTO; 400 / 404 on failure.</returns>
    [HttpPut("topics/{sqid}/translations/{language}")]
    [Consumes("application/json")]
    public async Task<IActionResult> UpsertTranslationAsync(
        [FromRoute] string sqid,
        [FromRoute] string language,
        [FromBody] HelpTopicTranslationUpsertDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _translations.UpsertAsync(sqid, language, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Flips a translation's approval flag and emits an audit row.</summary>
    /// <param name="sqid">Sqid-encoded id of the translation row.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO; 404 when missing.</returns>
    [HttpPost("translations/{sqid}/approve")]
    public async Task<IActionResult> ApproveTranslationAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _translations.ApproveAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a service-layer failure to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>The mapped action result.</returns>
    private IActionResult MapFailure(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> to an HTTP status code.</summary>
    /// <param name="code">Stable error code.</param>
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
