using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Templates;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2003 / R0133 — admin REST surface over the template-language coverage
/// registry. Restricted to the
/// <see cref="AuthorizationComposition.CnasAdmin"/> policy because the
/// surface exposes the per-template translation worklist + the persisted
/// finding backlog.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>GET  /api/admin/templates/coverage?requiredLanguages=ro,en,ru&amp;onlyApproved=true&amp;includeRetiredTemplates=false&amp;skip=0&amp;take=100</c> — compute the projection (no persistence).</item>
///   <item><c>POST /api/admin/templates/coverage/scan</c> — record a coverage run, inserting findings + emitting audit per gap.</item>
///   <item><c>GET  /api/admin/templates/coverage/findings?acknowledged=false&amp;missingLanguage=ru&amp;skip=0&amp;take=100</c> — list findings.</item>
///   <item><c>POST /api/admin/templates/coverage/findings/{sqid}/acknowledge</c> — acknowledge a finding.</item>
/// </list>
/// </para>
/// </remarks>
/// <param name="service">Template-language coverage service façade.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/templates/coverage")]
public sealed class TemplateLanguageCoverageAdminController(
    ITemplateLanguageCoverageService service) : ControllerBase
{
    private readonly ITemplateLanguageCoverageService _service = service;

    /// <summary>
    /// Computes the coverage projection for the supplied filter. No
    /// persistence side-effects — safe to call from the dashboard polling
    /// loop.
    /// </summary>
    /// <param name="requiredLanguages">Comma-separated lowercase ISO 639-1 codes; null falls back to RO/EN/RU.</param>
    /// <param name="onlyApproved">When true (default) only approved variants count as coverage.</param>
    /// <param name="includeRetiredTemplates">When true include retired (IsActive=false) templates in the scan.</param>
    /// <param name="skip">Page offset (≥ 0; default 0).</param>
    /// <param name="take">Page size (1..500; default 100).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the report, or 400 on validation failure.</returns>
    [HttpGet]
    public async Task<ActionResult<TemplateLanguageCoverageReportDto>> GetCoverageAsync(
        [FromQuery] string? requiredLanguages = null,
        [FromQuery] bool onlyApproved = true,
        [FromQuery] bool includeRetiredTemplates = false,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        var filter = new TemplateLanguageCoverageFilterDto(
            RequiredLanguages: ParseLanguageCsv(requiredLanguages),
            OnlyApproved: onlyApproved,
            IncludeRetiredTemplates: includeRetiredTemplates,
            Skip: skip,
            Take: take);
        var result = await _service.ComputeCoverageAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<TemplateLanguageCoverageReportDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Records a coverage run — runs the projection AND inserts deduped
    /// findings per detected gap, emitting a Critical audit per new
    /// insertion.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the report, or 400 / 500 on failure.</returns>
    [HttpPost("scan")]
    public async Task<ActionResult<TemplateLanguageCoverageReportDto>> ScanAsync(
        CancellationToken cancellationToken = default)
    {
        // Canonical default filter for operator-driven runs — same shape as
        // the scheduled job but tagged with a manual trigger via the
        // caller-context UserSqid lookup inside the service.
        var filter = new TemplateLanguageCoverageFilterDto(
            RequiredLanguages: null,
            OnlyApproved: true,
            IncludeRetiredTemplates: false,
            Skip: 0,
            Take: 100);
        var result = await _service.RecordCoverageRunAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<TemplateLanguageCoverageReportDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists persisted findings filtered by acknowledgement state + missing language.</summary>
    /// <param name="acknowledged">Optional acknowledgement-state filter — null matches both.</param>
    /// <param name="missingLanguage">Optional lowercase ISO 639-1 language filter — null matches any.</param>
    /// <param name="skip">Page offset (≥ 0; default 0).</param>
    /// <param name="take">Page size (1..200; default 50).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the page, or 400 on validation failure.</returns>
    [HttpGet("findings")]
    public async Task<ActionResult<TemplateLanguageCoverageFindingPageDto>> ListFindingsAsync(
        [FromQuery] bool? acknowledged = null,
        [FromQuery] string? missingLanguage = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new TemplateLanguageCoverageFindingFilterDto(
            Acknowledged: acknowledged,
            MissingLanguage: missingLanguage,
            Skip: skip,
            Take: take);
        var result = await _service.ListFindingsAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<TemplateLanguageCoverageFindingPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Acknowledges a coverage finding with an operator-supplied note.</summary>
    /// <param name="sqid">Sqid-encoded finding id.</param>
    /// <param name="input">Acknowledgement payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO, or 400 / 404 / 409.</returns>
    [HttpPost("findings/{sqid}/acknowledge")]
    [Consumes("application/json")]
    public async Task<ActionResult<TemplateLanguageCoverageFindingDto>> AcknowledgeAsync(
        string sqid,
        [FromBody] TemplateLanguageCoverageAcknowledgeInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.AcknowledgeFindingAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<TemplateLanguageCoverageFindingDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Parses a comma-separated list of language codes (e.g. <c>"ro,en,ru"</c>)
    /// into a normalised lowercase list. Returns null when the input is
    /// null/whitespace so the service falls back to the canonical default.
    /// </summary>
    /// <param name="csv">Comma-separated language code list, may be null.</param>
    /// <returns>Trimmed lowercase codes, or null when no input was supplied.</returns>
    private static System.Collections.Generic.List<string>? ParseLanguageCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return null;
        }
        var parts = csv.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }
        var result = new System.Collections.Generic.List<string>(parts.Length);
        foreach (var p in parts)
        {
            result.Add(p.ToLowerInvariant());
        }
        return result;
    }

    /// <summary>
    /// Translates a failed <see cref="Result{T}"/> into the appropriate
    /// <see cref="ActionResult"/>: <c>INVALID_SQID</c> / <c>VALIDATION_FAILED</c>
    /// → 400, <c>NOT_FOUND</c> → 404, <c>CONFLICT</c> → 409, anything else → 500.
    /// </summary>
    /// <typeparam name="T">DTO type that would have been returned on success.</typeparam>
    /// <param name="errorCode">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="errorMessage">Human-readable description.</param>
    /// <returns>An <see cref="ActionResult{T}"/> carrying the appropriate HTTP status.</returns>
    private ActionResult<T> MapFailure<T>(string errorCode, string errorMessage)
        => errorCode switch
        {
            ErrorCodes.InvalidSqid => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.ValidationFailed => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
