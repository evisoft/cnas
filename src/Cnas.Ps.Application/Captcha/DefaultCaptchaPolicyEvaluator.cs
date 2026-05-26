using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Captcha;

/// <summary>
/// R0507 / TOR CF 01.10 — default <see cref="ICaptchaPolicyEvaluator"/>
/// implementation. Treats a public-catalog query as "broad" (CAPTCHA-required)
/// when neither the free-text <c>Q</c> nor the <c>Category</c> filter is
/// supplied. The classification is intentionally permissive — narrow queries
/// stay friction-free; the rate limiter is the volumetric guard.
/// </summary>
public sealed class DefaultCaptchaPolicyEvaluator : ICaptchaPolicyEvaluator
{
    /// <inheritdoc />
    public bool RequireCaptcha(PublicCatalogListQueryDto? query)
    {
        if (query is null)
        {
            // Defensive default — a missing envelope is treated as broad so
            // the CAPTCHA gate trips closed rather than open. A controller
            // would never pass null in production but this keeps the unit
            // surface deterministic.
            return true;
        }

        var hasQ = !string.IsNullOrWhiteSpace(query.Q);
        var hasCategory = !string.IsNullOrWhiteSpace(query.Category);

        // Narrowing filter present → query is bounded; the rate limiter
        // alone is sufficient. Otherwise we treat the call as a broad sweep
        // and demand the user solve a CAPTCHA challenge.
        return !(hasQ || hasCategory);
    }
}
