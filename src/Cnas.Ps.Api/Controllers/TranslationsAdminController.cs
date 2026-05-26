using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Localization;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0210 / TOR UI 007 / CF 17.16 — admin REST surface over the translation-key
/// registry. Restricted to the <see cref="AuthorizationComposition.CnasAdmin"/>
/// policy. Pairs with the public read endpoint exposed by
/// <see cref="TranslationsPublicController"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>GET    /api/admin/translations/keys?module=</c>                          — paged list.</item>
///   <item><c>GET    /api/admin/translations/keys/{sqid}</c>                            — fetch one key + every value.</item>
///   <item><c>POST   /api/admin/translations/keys</c>                                   — create a new key.</item>
///   <item><c>PUT    /api/admin/translations/keys/{sqid}</c>                            — update metadata.</item>
///   <item><c>PUT    /api/admin/translations/keys/{sqid}/values/{language}</c>          — upsert per-language value.</item>
///   <item><c>POST   /api/admin/translations/values/{sqid}/approve</c>                  — flip the approval flag (Critical audit).</item>
/// </list>
/// </para>
/// </remarks>
/// <param name="keys">Underlying key service.</param>
/// <param name="values">Underlying value service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/translations")]
public sealed class TranslationsAdminController(
    ITranslationKeyService keys,
    ITranslationValueService values) : ControllerBase
{
    private readonly ITranslationKeyService _keys = keys;
    private readonly ITranslationValueService _values = values;

    /// <summary>
    /// Lists every active translation key, optionally filtered by module. Each row
    /// carries every persisted per-language value.
    /// </summary>
    /// <param name="module">Optional module filter (e.g. <c>Public</c>).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the list on success; 401 when anonymous.</returns>
    [HttpGet("keys")]
    public async Task<IActionResult> ListKeysAsync(
        [FromQuery] string? module = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _keys.ListAsync(module, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Fetches a single key by Sqid id with every persisted value rolled up.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the key row.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the key DTO on success; 404 when missing.</returns>
    [HttpGet("keys/{sqid}")]
    public async Task<IActionResult> GetKeyAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _keys.GetAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Creates a new translation key.
    /// </summary>
    /// <param name="input">Authoring payload (Code, Description, Module).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the resulting DTO on success; 400 / 409 on failure.</returns>
    [HttpPost("keys")]
    [Consumes("application/json")]
    public async Task<IActionResult> CreateKeyAsync(
        [FromBody] TranslationKeyUpsertDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _keys.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Updates an existing key's metadata. The code itself is immutable.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the key row.</param>
    /// <param name="input">Authoring payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 on success; 404 when missing; 400 on bad input.</returns>
    [HttpPut("keys/{sqid}")]
    [Consumes("application/json")]
    public async Task<IActionResult> UpdateKeyAsync(
        [FromRoute] string sqid,
        [FromBody] TranslationKeyUpsertDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _keys.UpdateAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Idempotent upsert for one (key, language) translation value. Inserts on first
    /// call, updates otherwise; the natural-key UNIQUE on the value table is the
    /// safety net.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the parent key.</param>
    /// <param name="language">ISO-639-1 language code.</param>
    /// <param name="input">Authoring payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the resulting value DTO; 400 / 404 on failure.</returns>
    [HttpPut("keys/{sqid}/values/{language}")]
    [Consumes("application/json")]
    public async Task<IActionResult> UpsertValueAsync(
        [FromRoute] string sqid,
        [FromRoute] string language,
        [FromBody] TranslationValueUpsertDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _values.UpsertAsync(sqid, language, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Flips a value's approval flag to <c>true</c> and emits a Critical
    /// <c>TRANSLATION.APPROVED</c> audit row.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the value row.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO; 404 when missing.</returns>
    [HttpPost("values/{sqid}/approve")]
    public async Task<IActionResult> ApproveValueAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _values.ApproveAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : MapFailure(result.ErrorCode, result.ErrorMessage);
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
