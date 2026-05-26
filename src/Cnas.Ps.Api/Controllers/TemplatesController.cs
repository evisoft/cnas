using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// UC17 — Document-template administration REST surface. After phase 2B the surface
/// exposes five routes:
/// <list type="bullet">
///   <item><c>GET /api/templates</c> — lists every template currently registered (DI-baked
///         union with persistent rows; persistent wins on code collision).</item>
///   <item><c>GET /api/templates/{code}</c> — returns the single catalog row for one
///         template, or 404 when no template carries the requested code.</item>
///   <item><c>POST /api/templates</c> — multipart upload of a new persistent template
///         (or a new version of an existing one).</item>
///   <item><c>GET /api/templates/{code}/download</c> — streams the current binary of a
///         persistent template back to the caller; 404 for DI-baked-only codes.</item>
///   <item><c>POST /api/templates/{code}/render</c> — phase 2B: renders an
///         operator-uploaded persistent template by substituting <c>{{key}}</c>
///         placeholders with values from a runtime dictionary; streams the rendered
///         DOCX back inline.</item>
/// </list>
/// The first four routes require the <see cref="AuthorizationComposition.CnasAdmin"/>
/// policy — template management is functional-administration territory; lower-privileged
/// staff roles (<see cref="AuthorizationComposition.CnasUser"/>,
/// <see cref="AuthorizationComposition.CnasDecider"/>) must not see the catalog. The
/// render route deliberately broadens the gate to
/// <see cref="AuthorizationComposition.CnasUser"/> via a method-level
/// <see cref="AuthorizeAttribute"/> because rendering is a RUNTIME UTILITY (operators
/// submitting workflow runs and integration consumers need to materialise the bytes),
/// whereas catalog manipulation remains an admin-only surface. The list / inspect / render
/// routes are user-partitioned via the
/// <see cref="RateLimitingPolicies.Authenticated"/> limiter; the upload route uses the
/// stricter <see cref="RateLimitingPolicies.Upload"/> bucket because byte transfers are
/// expensive and must not exhaust the wider authenticated allowance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 2B (this commit)</b> — adds the <c>POST /api/templates/{code}/render</c>
/// route. The controller wires
/// <see cref="IDocumentGenerationService.GenerateFromUploadedTemplateAsync"/> to the
/// HTTP boundary; the renderer is unchanged. Placeholder semantics inherit the
/// renderer's contract: keys missing from the supplied dictionary leave their
/// <c>{{placeholder}}</c> markers verbatim in the output — they do not produce a 400.
/// </para>
/// </remarks>
/// <param name="templates">Underlying admin service that projects + persists the template registry.</param>
/// <param name="documents">UC17 phase 2B — document-generation service used by the render route.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/templates")]
public sealed class TemplatesController(
    ITemplateAdminService templates,
    IDocumentGenerationService documents) : ControllerBase
{
    /// <summary>
    /// DOCX MIME type used by every render route. Centralised here so the
    /// <c>Content-Type</c> header stays in lock-step with the Infrastructure-layer
    /// <c>DocumentGenerationService</c> (which uses the same constant under the same
    /// name) and the magic-byte sniff in the upload path.
    /// </summary>
    private const string DocxMimeType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private readonly ITemplateAdminService _templates = templates;
    private readonly IDocumentGenerationService _documents = documents;

    /// <summary>
    /// Lists every template code currently registered — DI-baked
    /// <c>IDocxTemplate</c> singletons unioned with persistent rows from
    /// <c>DocumentTemplates</c>. Used by the admin front-end to populate the
    /// template-picker drop-down and the diagnostic table.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the catalog; 400 ProblemDetails on unexpected service failure.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TemplateCatalogEntry>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _templates.ListAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<IReadOnlyList<TemplateCatalogEntry>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Returns the catalog row for a single template identified by its stable
    /// <paramref name="code"/>. Match is case-insensitive. Persistent rows take
    /// precedence over DI-baked rows when both registries carry the same code.
    /// </summary>
    /// <param name="code">Stable template code (e.g. <c>refuz-aplicare</c>).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200, 404, or 400 per the service result.</returns>
    [HttpGet("{code}")]
    public async Task<ActionResult<TemplateCatalogEntry>> GetByCodeAsync(
        [FromRoute] string code,
        CancellationToken cancellationToken = default)
    {
        var result = await _templates.GetAsync(code, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<TemplateCatalogEntry>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Uploads a new persistent template OR a new version of an existing one. Accepts a
    /// multipart/form-data body with a <c>file</c> part (the DOCX binary) plus the form
    /// fields <c>code</c>, <c>name</c>, and an optional <c>description</c>. Throttled by
    /// the stricter <see cref="RateLimitingPolicies.Upload"/> policy at the method level
    /// so abuse of the upload path can't exhaust storage bandwidth even if the caller
    /// stays under the wider <see cref="RateLimitingPolicies.Authenticated"/> allowance
    /// applied at the controller level — method-level attributes take precedence over
    /// controller-level ones for limiter resolution. The 5-MiB application-level cap
    /// drives <see cref="RequestSizeLimitAttribute"/>; the service-level
    /// <c>MaxTemplateSize</c> enforces the same cap during the streaming copy so a
    /// caller who somehow bypasses the framework limit still cannot persist an
    /// oversized blob.
    /// </summary>
    /// <param name="file">Multipart file part — the DOCX binary.</param>
    /// <param name="code">Stable kebab-case template code (form field).</param>
    /// <param name="name">Human-readable display name (form field).</param>
    /// <param name="description">Optional free-text usage note (form field).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 201 Created with the new <see cref="TemplateCatalogEntry"/> on success; 400
    /// ProblemDetails on validation / magic-byte / size failures.
    /// </returns>
    [HttpPost]
    [EnableRateLimiting(RateLimitingPolicies.Upload)]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<ActionResult<TemplateCatalogEntry>> UploadAsync(
        IFormFile file,
        [FromForm] string code,
        [FromForm] string name,
        [FromForm] string? description,
        CancellationToken cancellationToken = default)
    {
        if (file is null)
        {
            // Null IFormFile means the multipart binding could not locate the "file"
            // part. Return 400 directly so the service never sees a malformed call —
            // the alternative would be a NullReferenceException in OpenReadStream().
            return Problem("A 'file' multipart part is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        await using var stream = file.OpenReadStream();
        var result = await _templates.UploadAsync(
            code,
            name,
            description,
            stream,
            file.ContentType,
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return MapFailureGeneric<TemplateCatalogEntry>(result.ErrorCode, result.ErrorMessage);
        }

        // 201 Created — point the Location header at GET /api/templates/{code} so the
        // caller can navigate to the just-created row without a follow-up request to
        // discover its URL.
        return CreatedAtAction(
            nameof(GetByCodeAsync),
            new { code = result.Value.Code },
            result.Value);
    }

    /// <summary>
    /// Streams the current binary of a persistent template back to the caller. The
    /// response carries the original MIME type, a meaningful filename via
    /// <c>Content-Disposition</c>, and the bytes themselves. DI-baked-only codes return
    /// 404 because they have no stored blob (phase 2B may revisit this once the
    /// renderer pipeline is unified).
    /// </summary>
    /// <param name="code">Stable template code (case-insensitive).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with a <see cref="FileStreamResult"/> on success; 404 when no persistent row
    /// exists for the code; 400 ProblemDetails for any other service failure.
    /// </returns>
    [HttpGet("{code}/download")]
    public async Task<ActionResult> DownloadAsync(
        [FromRoute] string code,
        CancellationToken cancellationToken = default)
    {
        var result = await _templates.DownloadAsync(code, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return StatusForCode(result.ErrorCode) == StatusCodes.Status404NotFound
                ? NotFound()
                : Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }

        var dl = result.Value;
        return new FileStreamResult(dl.Content, dl.ContentType)
        {
            FileDownloadName = dl.SuggestedFileName,
        };
    }

    /// <summary>
    /// UC17 phase 2B — Renders an operator-uploaded persistent DOCX template by
    /// substituting every <c>{{placeholder}}</c> marker in the stored document with
    /// the matching value from the request body's <c>data</c> dictionary, then
    /// streams the rendered DOCX back inline. Bypasses the dossier-centric pipeline
    /// (no dossier load, no decision-engine re-evaluation, no <c>Document</c> row
    /// insertion, no audit emission) — this is a render-only utility, not a
    /// document-persistence operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Authorization choice — <see cref="AuthorizationComposition.CnasUser"/>.</b>
    /// Unlike the four catalog-management routes (CnasAdmin), the render surface is
    /// open to any authenticated CNAS staff role. Rendering is a runtime utility:
    /// workflow steps, the future report subsystem, and integration tests all need to
    /// materialise the bytes for a template the system already has, without elevating
    /// to admin. Restricting render to admin would force every operator who
    /// legitimately needs to render a workflow document to be a functional
    /// administrator, which inverts the principle of least privilege. The catalog
    /// itself remains admin-only — what is restricted is the <em>shape</em> of the
    /// catalogue, not the use of its templates.
    /// </para>
    /// <para>
    /// <b>Rate limit — <see cref="RateLimitingPolicies.Authenticated"/>.</b> A render
    /// call is comparable in cost to a Read action: one row lookup plus a small DOCX
    /// in-memory transformation. It does not write to storage or persist anything,
    /// so the stricter <see cref="RateLimitingPolicies.Upload"/> bucket would be
    /// over-protective. Throttling stays at the controller-default authenticated
    /// budget.
    /// </para>
    /// <para>
    /// <b>Unknown-placeholder contract.</b> Placeholders that appear in the stored
    /// DOCX but have no entry in the request <c>data</c> dictionary are LEFT VERBATIM
    /// in the rendered output — they do not throw and do not produce a 400. This
    /// inherits the renderer's lenient substitution contract documented on
    /// <see cref="IUploadedTemplateRenderer.RenderAsync"/>; callers do not need to
    /// scrape the template for required keys before posting.
    /// </para>
    /// </remarks>
    /// <param name="code">Stable kebab-case template code (case-insensitive match).</param>
    /// <param name="body">Request body carrying the placeholder-value dictionary.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 OK with a <see cref="FileStreamResult"/> whose <c>Content-Type</c> is the
    /// DOCX MIME type and whose <c>Content-Disposition</c> filename is
    /// <c>{code}.docx</c>; 404 NotFound when no persistent template carries the
    /// requested code; 400 BadRequest when the request body cannot be deserialised
    /// or any other validation/service failure occurs.
    /// </returns>
    [HttpPost("{code}/render")]
    [Authorize(Policy = AuthorizationComposition.CnasUser)]
    public async Task<IActionResult> RenderAsync(
        [FromRoute] string code,
        [FromBody] RenderUploadedTemplateRequest body,
        CancellationToken cancellationToken = default)
    {
        // Defend against null body — the framework binder hands the action a null
        // record when the JSON payload cannot be deserialised (body absent, body is
        // the literal "null", body fails record-positional binding). Returning 400
        // here is symmetric with the Upload action's null-file defence and prevents
        // a NullReferenceException downstream.
        if (body is null)
        {
            return Problem(
                "A JSON request body with a 'data' field is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _documents
            .GenerateFromUploadedTemplateAsync(code, body.Data, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return StatusForCode(result.ErrorCode) == StatusCodes.Status404NotFound
                ? NotFound()
                : Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }

        // Wrap the rendered bytes in a fresh MemoryStream so the framework's
        // FileStreamResult lifetime (the runtime disposes the stream after the body
        // is written) does not corrupt the underlying byte[] cache. The stream is
        // not seekable beyond the buffered length — that's fine; FileStreamResult
        // reads sequentially.
        var stream = new MemoryStream(result.Value, writable: false);
        return new FileStreamResult(stream, DocxMimeType)
        {
            FileDownloadName = $"{code}.docx",
        };
    }

    /// <summary>Maps a <see cref="Result{T}"/> failure to an <see cref="ActionResult{T}"/>.</summary>
    /// <typeparam name="T">The DTO type the action would have returned on success.</typeparam>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 400 ProblemDetails as appropriate.</returns>
    private ActionResult<T> MapFailureGeneric<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>404 NotFound for <see cref="ErrorCodes.NotFound"/>, or 400 BadRequest otherwise.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.FileTooLarge => StatusCodes.Status400BadRequest,
        ErrorCodes.FileTypeMismatch => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
