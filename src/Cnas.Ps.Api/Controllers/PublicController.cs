using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Filters;
using Cnas.Ps.Contracts;
using Cnas.Ps.Application.PublicServices;
using Cnas.Ps.Application.UseCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// Public REST surface. Backs UC01 (public content) and UC02 (informational calculators).
/// Anonymous-accessible; rate-limited in-process by the <see cref="RateLimitingPolicies.Anonymous"/>
/// policy (CLAUDE.md §5.3 — 5 req/min IP bucket) on top of any gateway-level throttling,
/// AND gated by <see cref="RequireCaptchaAttribute"/> so single-call abuse that rotates
/// IPs (and thereby evades the rate-limit partition) still has to solve a Cloudflare
/// Turnstile challenge before the action runs (R0035). Production wires
/// <c>Cnas:Captcha:Turnstile</c> from the secrets manager; dev / integration tests set
/// <c>BypassForTesting</c> so the suite never hits Cloudflare from CI.
/// </summary>
[ApiController]
[EnableRateLimiting(RateLimitingPolicies.Anonymous)]
[RequireCaptcha]
[Route("api/public")]
public sealed class PublicController(
    IPublicContentService publicContent,
    IInformationServices information,
    IPublicKpiService publicKpis) : ControllerBase
{
    /// <summary>UC01 — list public content cards (paged, searchable).</summary>
    [HttpGet("content")]
    public async Task<ActionResult<PagedResult<PublicContentCard>>> SearchAsync(
        [FromQuery] string? query,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var req = new SearchRequest(query, null, null, null, false, new PageRequest(page, pageSize));
        var result = await publicContent.SearchAsync(req, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : Problem(result.ErrorMessage, statusCode: 400);
    }

    /// <summary>UC02 — retirement-age calculator.</summary>
    [HttpGet("calculators/retirement-age")]
    public async Task<ActionResult<RetirementAgeOutput>> CalcRetirementAsync(
        [FromQuery] DateOnly birthDate,
        [FromQuery] char sex,
        CancellationToken cancellationToken = default)
    {
        // Wire format on the DTO is string-typed (consistent with PensionSimulationInputDto.Gender
        // and AthletePensionAwardDto.BeneficiarySex); the controller still accepts the single-char
        // query parameter for backward compatibility and stringifies at the boundary.
        var result = await information.CalculateRetirementAgeAsync(new RetirementAgeInput(birthDate, sex.ToString()), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : Problem(result.ErrorMessage, statusCode: 400);
    }

    /// <summary>UC02 — application status lookup by reference number.</summary>
    [HttpGet("calculators/application-status")]
    public async Task<ActionResult<ApplicationStatusOutput>> GetStatusAsync(
        [FromQuery] string referenceNumber,
        CancellationToken cancellationToken = default)
    {
        var result = await information.GetApplicationStatusAsync(referenceNumber, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : NotFound();
    }

    /// <summary>
    /// R0500 / UC01 / TOR CF 01.02 — depersonalised public KPI snapshot
    /// (counts of contributors, insured persons, pending applications,
    /// decisions issued in the last 30 days, most-recent Treasury feed
    /// import timestamp). Anonymous-accessible, rate-limited by the
    /// surrounding policy. Marked
    /// <see cref="AllowAnonymousAttribute"/> defensively even though the
    /// controller class itself has no <c>[Authorize]</c> — the explicit
    /// marker survives any future class-level authorisation addition.
    /// </summary>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>200 with <see cref="PublicKpiSnapshotDto"/>.</returns>
    [HttpGet("kpis")]
    [AllowAnonymous]
    public async Task<ActionResult<PublicKpiSnapshotDto>> GetKpisAsync(CancellationToken cancellationToken = default)
    {
        var result = await publicKpis.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : Problem(result.ErrorMessage, statusCode: 500);
    }
}
