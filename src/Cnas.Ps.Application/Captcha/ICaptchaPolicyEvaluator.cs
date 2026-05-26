using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Captcha;

/// <summary>
/// R0507 / TOR CF 01.10 — decides whether a given anonymous public-catalog
/// search MUST present a CAPTCHA token before the response is served. Pure
/// function — no I/O — so it can be injected as a singleton and unit-tested
/// in isolation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Policy.</b> The default implementation classifies a query as "broad"
/// when no narrowing filter is present (no free-text <c>Q</c>, no
/// <c>Category</c>). Broad queries always require a CAPTCHA. Narrow queries
/// (e.g. anchored on a specific category) bypass the gate so the common
/// "give me services under 'pensions'" UX is friction-free.
/// </para>
/// </remarks>
public interface ICaptchaPolicyEvaluator
{
    /// <summary>
    /// Returns <c>true</c> when the supplied catalog query must present a
    /// CAPTCHA token; <c>false</c> when it can pass through unchallenged.
    /// </summary>
    /// <param name="query">Inbound filter envelope (may be null on early-bind tests).</param>
    /// <returns><c>true</c> iff a CAPTCHA token is required.</returns>
    bool RequireCaptcha(PublicCatalogListQueryDto? query);
}
