using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Captcha;
using Cnas.Ps.Application.PublicCatalog;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0502 / R0504 / R0505 / TOR CF 01.05 / CF 01.06 / CF 01.08 — public,
/// anonymous-accessible REST surface over <see cref="IPublicCatalogService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Route table:
/// <list type="bullet">
///   <item><c>GET /api/public-catalog</c> — paged list (200 / 422 / 400).</item>
///   <item><c>GET /api/public-catalog/export.csv</c> — full filtered set as CSV (200 / 422 / 400).</item>
///   <item><c>GET /api/public-catalog/export.xlsx</c> — 501 ProblemDetails (deferred).</item>
///   <item><c>GET /api/public-catalog/export.pdf</c> — 501 ProblemDetails (deferred).</item>
/// </list>
/// </para>
/// <para>
/// <b>Anonymous-accessible.</b> No <c>[Authorize]</c> attribute. The
/// <see cref="RateLimitingPolicies.Anonymous"/> policy applies (R0035 — 5 req/min IP
/// partition) — the same throttle that protects <see cref="PublicController"/>.
/// A dedicated <c>PublicCatalogAnonymous</c> policy is deferred until the
/// catalog endpoint's traffic profile is measured in production.
/// </para>
/// <para>
/// <b>Sqid invariant (CLAUDE.md RULE 3).</b> Every <c>Id</c> field on the
/// response DTOs is a Sqid string. The endpoint does not accept any inbound
/// identifiers (filtering is by free-text + category, not by id).
/// </para>
/// </remarks>
/// <param name="catalog">Underlying read façade (per-request scope).</param>
/// <param name="captchaPolicy">Decides which queries must present a CAPTCHA token (R0507).</param>
/// <param name="captcha">Self-issued CAPTCHA challenge service (R0507).</param>
[ApiController]
[EnableRateLimiting(RateLimitingPolicies.Anonymous)]
[Route("api/public-catalog")]
public sealed class PublicCatalogController(
    IPublicCatalogService catalog,
    ICaptchaPolicyEvaluator captchaPolicy,
    ICaptchaChallengeService captcha) : ControllerBase
{
    /// <summary>The read façade for the public services-catalog.</summary>
    private readonly IPublicCatalogService _catalog = catalog;

    /// <summary>Decides whether the incoming query is "broad" and must present a CAPTCHA token (R0507).</summary>
    private readonly ICaptchaPolicyEvaluator _captchaPolicy = captchaPolicy;

    /// <summary>Verifies the inbound <c>X-Captcha-Token</c> against the in-memory challenge store (R0507).</summary>
    private readonly ICaptchaChallengeService _captcha = captcha;

    /// <summary>HTTP header carrying the previously-verified CAPTCHA token (R0507).</summary>
    public const string CaptchaTokenHeader = "X-Captcha-Token";

    /// <summary>Stable ProblemDetails <c>type</c> URI for the CAPTCHA-required failure mode (R0507).</summary>
    private const string CaptchaRequiredProblemType = "https://cnas/captcha/required";

    /// <summary>
    /// Stable ProblemDetails <c>type</c> URI for the "query too broad" failure
    /// mode. Stable across versions — UI code matches on this string to render
    /// the refinement prompt. Identical to the value used by
    /// <see cref="SolicitantsController"/>.
    /// </summary>
    private const string QueryTooBroadProblemType = "https://cnas/queries/too-broad";

    /// <summary>
    /// Stable ProblemDetails <c>type</c> URI for the export-format-not-implemented
    /// failure mode. Returned from the PDF / XLSX endpoints until those renderers
    /// are wired up.
    /// </summary>
    private const string ExportNotImplementedProblemType = "https://cnas/exports/not-implemented";

    /// <summary>
    /// Hard upper bound mirrored from
    /// <see cref="Cnas.Ps.Application.Validators.PublicCatalogListQueryValidator.MaxTake"/>.
    /// Defense in depth — the validator already rejected larger values, but the
    /// controller clamps anyway in case the validator is ever bypassed (e.g.
    /// programmatic test code).
    /// </summary>
    private const int MaxTake = 200;

    /// <summary>
    /// Paged list of public services-catalog rows. Anonymous-accessible.
    /// </summary>
    /// <param name="q">Optional free-text query.</param>
    /// <param name="category">Optional category code (equality match).</param>
    /// <param name="sort">Sort key — Relevance / Alphabetical / Created / Updated.</param>
    /// <param name="skip">0-based offset.</param>
    /// <param name="take">Page size (capped at 200).</param>
    /// <param name="language">ISO-639-1 language code; default <c>"ro"</c>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with the paged list on success; 422 ProblemDetails with the budget
    /// verdict in <c>extensions["budget"]</c> when the query exceeds the
    /// registry budget; 400 ProblemDetails on validation failure.
    /// </returns>
    [HttpGet]
    public async Task<ActionResult<PagedResult<PublicCatalogListItemDto>>> ListAsync(
        [FromQuery] string? q = null,
        [FromQuery] string? category = null,
        [FromQuery] string sort = "Relevance",
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        [FromQuery] string? language = "ro",
        CancellationToken cancellationToken = default)
    {
        var dto = new PublicCatalogListQueryDto(
            Q: q,
            Category: category,
            Sort: sort,
            Skip: skip,
            Take: Math.Clamp(take, 1, MaxTake),
            Language: language);

        // R0507 / TOR CF 01.10 — broad-query CAPTCHA gate. Narrow queries
        // (with a Q or Category filter) pass through unchallenged; broad
        // queries demand a recently-verified X-Captcha-Token header. The
        // rate limiter still guards both paths volumetrically.
        if (_captchaPolicy.RequireCaptcha(dto))
        {
            var token = HttpContext.Request.Headers[CaptchaTokenHeader].ToString();
            var normalisedToken = string.IsNullOrWhiteSpace(token) ? null : token;
            var ok = await _captcha
                .IsRecentlyVerifiedAsync(normalisedToken, cancellationToken)
                .ConfigureAwait(false);
            if (!ok)
            {
                return CaptchaRequiredProblem();
            }
            // R0507 — one-shot token contract. Even though the post-verify
            // window allows a brief grace period, the token MUST NOT be
            // replayable for further broad searches inside that window. The
            // atomic ConsumeAsync transitions the entry from verified to
            // consumed (a concurrent peer trying the same token here loses
            // the CAS race and gets ALREADY_CONSUMED — surfaced to the
            // caller as the same 403 CAPTCHA-required prompt so the UI
            // re-mints a fresh challenge).
            var consume = await _captcha.ConsumeAsync(normalisedToken, cancellationToken).ConfigureAwait(false);
            if (consume.IsFailure)
            {
                return CaptchaRequiredProblem();
            }
        }

        var result = await _catalog.ListAsync(dto, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        if (string.Equals(result.ErrorCode, ErrorCodes.QueryTooBroad, StringComparison.Ordinal))
        {
            return QueryTooBroadProblem(result.ErrorMessage, _catalog.LastBudgetVerdict);
        }

        return Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Exports the filtered + sorted catalog as a CSV file (RFC 4180, UTF-8
    /// with BOM). Anonymous-accessible; gated by the same budget guard as
    /// <see cref="ListAsync"/>.
    /// </summary>
    /// <param name="q">Optional free-text query.</param>
    /// <param name="category">Optional category code.</param>
    /// <param name="sort">Sort key — Relevance / Alphabetical / Created / Updated.</param>
    /// <param name="language">ISO-639-1 language code; default <c>"ro"</c>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with the CSV body on success; 422 on too-broad export; 400 on
    /// validation failure.
    /// </returns>
    [HttpGet("export.csv")]
    public async Task<IActionResult> ExportCsvAsync(
        [FromQuery] string? q = null,
        [FromQuery] string? category = null,
        [FromQuery] string sort = "Relevance",
        [FromQuery] string? language = "ro",
        CancellationToken cancellationToken = default)
    {
        // Export ignores Skip / Take by design — the budget guard already caps
        // the materialised set. Pass the validator-friendly defaults so the
        // shared DTO shape doesn't trip the [1, 200] Take rule.
        var dto = new PublicCatalogListQueryDto(
            Q: q,
            Category: category,
            Sort: sort,
            Skip: 0,
            Take: 1,
            Language: language);

        var result = await _catalog.ExportCsvAsync(dto, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return File(
                fileContents: result.Value,
                contentType: "text/csv; charset=utf-8",
                fileDownloadName: "public-catalog.csv");
        }

        if (string.Equals(result.ErrorCode, ErrorCodes.QueryTooBroad, StringComparison.Ordinal))
        {
            return QueryTooBroadProblem(result.ErrorMessage, _catalog.LastBudgetVerdict);
        }

        return Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// XLSX export — DEFERRED. Returns 501 ProblemDetails until the spreadsheet
    /// renderer is wired up.
    /// </summary>
    /// <returns>501 ProblemDetails with stable <c>type</c> URI.</returns>
    [HttpGet("export.xlsx")]
    public IActionResult ExportXlsx() => ExportNotImplemented("xlsx");

    /// <summary>
    /// PDF export — DEFERRED. Returns 501 ProblemDetails until the PDF renderer
    /// is wired up.
    /// </summary>
    /// <returns>501 ProblemDetails with stable <c>type</c> URI.</returns>
    [HttpGet("export.pdf")]
    public IActionResult ExportPdf() => ExportNotImplemented("pdf");

    /// <summary>
    /// Builds the 403 ProblemDetails returned when the caller's query is
    /// classified "broad" and no recently-verified CAPTCHA token was
    /// presented (R0507). The UI hooks the stable <c>type</c> URI to render
    /// the captcha-challenge prompt before re-submitting the search.
    /// </summary>
    /// <returns>The 403 ObjectResult.</returns>
    private ObjectResult CaptchaRequiredProblem()
    {
        var problem = new ProblemDetails
        {
            Type = CaptchaRequiredProblemType,
            Title = "A CAPTCHA challenge must be solved before this broad search can run.",
            Detail = "Issue a fresh challenge via /api/captcha/challenge, verify it via /api/captcha/verify, then resubmit with X-Captcha-Token header.",
            Status = StatusCodes.Status403Forbidden,
        };
        problem.Extensions["errorCode"] = ErrorCodes.CaptchaTokenMissing;
        problem.Extensions["captchaChallengeUrl"] = "/api/captcha/challenge";
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status403Forbidden,
            ContentTypes = { "application/problem+json" },
        };
    }

    /// <summary>
    /// Builds the 501 ProblemDetails returned by the deferred export endpoints.
    /// The format name lives in <c>extensions["format"]</c> so a single
    /// problem-type URI carries both deferred formats without needing two URIs.
    /// </summary>
    /// <param name="format">Short format name (<c>"pdf"</c> / <c>"xlsx"</c>).</param>
    /// <returns>The 501 ObjectResult.</returns>
    private ObjectResult ExportNotImplemented(string format)
    {
        var problem = new ProblemDetails
        {
            Type = ExportNotImplementedProblemType,
            Title = "This export format is not implemented yet.",
            Detail = $"The {format} export endpoint is reserved; use export.csv until the renderer is wired up.",
            Status = StatusCodes.Status501NotImplemented,
        };
        problem.Extensions["format"] = format;
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status501NotImplemented,
            ContentTypes = { "application/problem+json" },
        };
    }

    /// <summary>
    /// Builds the 422 ProblemDetails for a too-broad query. Mirrors the
    /// contract used by <see cref="SolicitantsController"/> so the UI can share
    /// a single rendering pipeline.
    /// </summary>
    /// <param name="detail">Human-readable detail from the service failure.</param>
    /// <param name="verdict">The most recent budget verdict; nullable.</param>
    /// <returns>The 422 ObjectResult.</returns>
    private ObjectResult QueryTooBroadProblem(string? detail, QueryBudgetVerdict? verdict)
    {
        var problem = new ProblemDetails
        {
            Type = QueryTooBroadProblemType,
            Title = "The query is too broad and would exceed the registry budget.",
            Detail = detail,
            Status = StatusCodes.Status422UnprocessableEntity,
        };
        problem.Extensions["budget"] = ToDto(verdict);
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status422UnprocessableEntity,
            ContentTypes = { "application/problem+json" },
        };
    }

    /// <summary>
    /// Translates a service-layer <see cref="QueryBudgetVerdict"/> to the wire
    /// DTO. A <c>null</c> verdict surfaces as a structurally-empty DTO so
    /// callers can rely on the shape of <c>extensions["budget"]</c>.
    /// </summary>
    /// <param name="verdict">Verdict carried back from the service; nullable.</param>
    /// <returns>The wire DTO.</returns>
    private static QueryBudgetVerdictDto ToDto(QueryBudgetVerdict? verdict)
    {
        if (verdict is null)
        {
            return new QueryBudgetVerdictDto(string.Empty, 0, 0, Array.Empty<QueryBudgetRefinementHintDto>());
        }
        var hints = verdict.Hints
            .Select(h => new QueryBudgetRefinementHintDto(h.FieldName, h.Severity, h.Reason))
            .ToList();
        return new QueryBudgetVerdictDto(
            verdict.Registry,
            verdict.EstimatedRowCount,
            verdict.Budget,
            hints);
    }
}
