using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Templates;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0133 / R0134 / TOR CF 17.16 / CF 17.17 — Admin REST surface for the per-language
/// <see cref="TemplateVariantOutputDto"/> registry plus the XML/CSV catalog
/// round-trip routes. Sister controller to <see cref="TemplatesController"/> — kept
/// separate so the existing UC17 catalog routes are not destabilised by the variant
/// + import/export additions.
/// </summary>
/// <remarks>
/// <para>
/// All routes require the <see cref="AuthorizationComposition.CnasAdmin"/> policy —
/// translating and approving variants is admin territory. The render-time
/// resolution (<see cref="ITemplateVariantResolver"/>) does NOT live here; it's
/// consumed internally by the renderer pipeline.
/// </para>
/// </remarks>
/// <param name="variants">Underlying variant service (upsert / approve / list).</param>
/// <param name="catalog">XML / CSV catalog import-export port.</param>
/// <param name="sqids">Sqid encode/decode service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/templates")]
public sealed class TemplatesAdminController(
    ITemplateVariantService variants,
    ITemplateCatalogPort catalog,
    ISqidService sqids) : ControllerBase
{
    private const string XmlMime = "application/xml";
    private const string CsvMime = "text/csv";

    private readonly ITemplateVariantService _variants = variants;
    private readonly ITemplateCatalogPort _catalog = catalog;
    private readonly ISqidService _sqids = sqids;

    /// <summary>
    /// Upserts the variant identified by <c>(templateSqid, language)</c>. Idempotent:
    /// a second call with the same route key replaces the existing row in place.
    /// </summary>
    /// <param name="templateSqid">Parent template Sqid (from the route).</param>
    /// <param name="language">Lower-case language code (from the route).</param>
    /// <param name="body">Translated payload (sans the Sqid+language fields, which come from the route).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the new variant DTO; 400/404 ProblemDetails on failure.</returns>
    [HttpPut("{templateSqid}/variants/{language}")]
    public async Task<ActionResult<TemplateVariantOutputDto>> UpsertAsync(
        [FromRoute] string templateSqid,
        [FromRoute] string language,
        [FromBody] TemplateVariantUpsertDto body,
        CancellationToken cancellationToken = default)
    {
        if (body is null)
        {
            return Problem("A JSON request body is required.", statusCode: StatusCodes.Status400BadRequest);
        }
        // Route parameters win over body — the route is the authoritative identity.
        var effective = body with { TemplateSqid = templateSqid, Language = language };
        var result = await _variants.UpsertAsync(effective, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<TemplateVariantOutputDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Lists every variant attached to the parent template.</summary>
    /// <param name="templateSqid">Parent template Sqid.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the list (possibly empty).</returns>
    [HttpGet("{templateSqid}/variants")]
    public async Task<ActionResult<IReadOnlyList<TemplateVariantOutputDto>>> ListAsync(
        [FromRoute] string templateSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(templateSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }
        var rows = await _variants.ListAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
        return Ok(rows);
    }

    /// <summary>
    /// Flips the variant's approval flag to <c>true</c>. Idempotent — re-approving
    /// already-approved rows still succeeds; the audit emission happens on every
    /// call so an admin's actions are always traceable.
    /// </summary>
    /// <param name="templateSqid">Parent template Sqid.</param>
    /// <param name="language">Lower-case language code identifying the variant row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 NoContent on success; 404 if the variant does not exist.</returns>
    [HttpPost("{templateSqid}/variants/{language}/approve")]
    public Task<IActionResult> ApproveAsync(
        [FromRoute] string templateSqid,
        [FromRoute] string language,
        CancellationToken cancellationToken = default)
        => SetApprovalAsync(templateSqid, language, true, cancellationToken);

    /// <summary>
    /// Flips the variant's approval flag back to <c>false</c>. Idempotent + audited
    /// (Critical severity).
    /// </summary>
    /// <param name="templateSqid">Parent template Sqid.</param>
    /// <param name="language">Lower-case language code identifying the variant row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 NoContent on success; 404 if the variant does not exist.</returns>
    [HttpPost("{templateSqid}/variants/{language}/unapprove")]
    public Task<IActionResult> UnapproveAsync(
        [FromRoute] string templateSqid,
        [FromRoute] string language,
        CancellationToken cancellationToken = default)
        => SetApprovalAsync(templateSqid, language, false, cancellationToken);

    /// <summary>
    /// Streams the entire template catalog as UTF-8 XML for offline editing in a
    /// desktop translation tool. The response is a parseable
    /// <c>&lt;TemplateCatalog&gt;</c> document; the DOCX blob bytes are NOT included.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with <c>application/xml</c> body.</returns>
    [HttpGet("export/xml")]
    [Produces(XmlMime)]
    public async Task<IActionResult> ExportXmlAsync(CancellationToken cancellationToken = default)
    {
        var result = await _catalog.ExportXmlAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? File(result.Value, XmlMime, "template-catalog.xml")
            : Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Streams the catalog as UTF-8 CSV with the documented header row.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with <c>text/csv</c> body.</returns>
    [HttpGet("export/csv")]
    [Produces(CsvMime)]
    public async Task<IActionResult> ExportCsvAsync(CancellationToken cancellationToken = default)
    {
        var result = await _catalog.ExportCsvAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? File(result.Value, CsvMime, "template-catalog.csv")
            : Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>Imports an edited XML catalog.</summary>
    /// <param name="file">Multipart file part carrying the XML payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the import report on success; 400 on validation failure.</returns>
    [HttpPost("import/xml")]
    public async Task<ActionResult<TemplateCatalogImportReportDto>> ImportXmlAsync(
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        if (file is null)
        {
            return Problem("A 'file' multipart part is required.", statusCode: StatusCodes.Status400BadRequest);
        }
        await using var stream = file.OpenReadStream();
        var result = await _catalog.ImportXmlAsync(stream, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>Imports an edited CSV catalog.</summary>
    /// <param name="file">Multipart file part carrying the CSV payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the import report on success; 400 on validation failure.</returns>
    [HttpPost("import/csv")]
    public async Task<ActionResult<TemplateCatalogImportReportDto>> ImportCsvAsync(
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        if (file is null)
        {
            return Problem("A 'file' multipart part is required.", statusCode: StatusCodes.Status400BadRequest);
        }
        await using var stream = file.OpenReadStream();
        var result = await _catalog.ImportCsvAsync(stream, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Common back-end for <see cref="ApproveAsync"/> / <see cref="UnapproveAsync"/>.
    /// Resolves the variant by (templateSqid, language) then delegates to the
    /// service-layer flip.
    /// </summary>
    /// <param name="templateSqid">Parent template Sqid.</param>
    /// <param name="language">Lower-case language code.</param>
    /// <param name="approved">Target value of the flag.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>204 NoContent on success; 404 when the variant doesn't exist.</returns>
    private async Task<IActionResult> SetApprovalAsync(
        string templateSqid,
        string language,
        bool approved,
        CancellationToken ct)
    {
        var decoded = _sqids.TryDecode(templateSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }
        var variant = await _variants.GetAsync(decoded.Value, language, ct).ConfigureAwait(false);
        if (variant is null)
        {
            return NotFound();
        }
        // Decode variant id from the Sqid the service emitted.
        var idDecoded = _sqids.TryDecode(variant.Id);
        if (idDecoded.IsFailure)
        {
            return Problem(idDecoded.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }
        var op = approved
            ? await _variants.ApproveAsync(idDecoded.Value, ct).ConfigureAwait(false)
            : await _variants.UnapproveAsync(idDecoded.Value, ct).ConfigureAwait(false);
        return op.IsSuccess
            ? NoContent()
            : Problem(op.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>Maps a failure result to a ProblemDetails ActionResult.</summary>
    /// <typeparam name="T">The DTO type the success path would have returned.</typeparam>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 or 400 ProblemDetails.</returns>
    private ActionResult<T> MapFailure<T>(string? code, string? message)
    {
        var status = code switch
        {
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest,
        };
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }
}
