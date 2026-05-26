using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Help;
using Cnas.Ps.Application.Localization;
using Cnas.Ps.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0225 / TOR UI 015 — public read surface over a single help topic.
/// Anonymous-accessible. Rate-limited by the
/// <see cref="RateLimitingPolicies.Anonymous"/> policy.
/// </summary>
/// <param name="resolver">Singleton cached resolver.</param>
[ApiController]
[EnableRateLimiting(RateLimitingPolicies.Anonymous)]
[Route("api/help")]
public sealed class HelpPublicController(IHelpResolver resolver) : ControllerBase
{
    private readonly IHelpResolver _resolver = resolver;

    /// <summary>
    /// Returns the topic identified by <paramref name="code"/> with every persisted
    /// translation; 404 when no topic matches.
    /// </summary>
    /// <param name="code">Stable kebab-case topic code.</param>
    /// <param name="language">Caller's language preference; informational.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the topic DTO; 404 on miss.</returns>
    [HttpGet("{code}")]
    public async Task<ActionResult<HelpTopicDto>> GetAsync(
        [FromRoute] string code,
        [FromQuery] string language = TranslationLanguages.Romanian,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return BadRequest();
        }
        var topic = await _resolver.GetByCodeAsync(code, language, cancellationToken).ConfigureAwait(false);
        return topic is null ? NotFound() : Ok(topic);
    }
}
