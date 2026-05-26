using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0210 / TOR UI 007 / CF 17.16 — public read surface over a single translation
/// key. Anonymous-accessible so the Blazor WASM client can resolve labels before
/// the user has signed in. Rate-limited by the
/// <see cref="RateLimitingPolicies.Anonymous"/> policy.
/// </summary>
/// <param name="resolver">Singleton cached resolver.</param>
[ApiController]
[EnableRateLimiting(RateLimitingPolicies.Anonymous)]
[Route("api/translations")]
public sealed class TranslationsPublicController(ITranslationResolver resolver) : ControllerBase
{
    private readonly ITranslationResolver _resolver = resolver;

    /// <summary>
    /// Resolves a single translation. The resolver applies the documented RO /
    /// code-as-fallback chain on miss so the response always carries a non-empty
    /// string.
    /// </summary>
    /// <param name="code">Stable kebab-case key (e.g. <c>pages.applications.list.title</c>).</param>
    /// <param name="language">ISO-639-1 language code; defaults to <c>ro</c>.</param>
    /// <returns>
    /// 200 with a JSON object <c>{ "code": ..., "language": ..., "text": ... }</c>.
    /// </returns>
    [HttpGet("{code}")]
    public IActionResult ResolveAsync(
        [FromRoute] string code,
        [FromQuery] string language = TranslationLanguages.Romanian)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return BadRequest();
        }
        var resolved = _resolver.Resolve(code, language);
        return Ok(new { code, language, text = resolved });
    }
}
