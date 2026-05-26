using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Reports;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0156 / TOR CF 09.02 / FLEX 003 — REST surface for the ad-hoc report builder.
/// Power users CRUD <see cref="ReportTemplateDto"/> rows and execute them via the
/// matching engine endpoints.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>GET    /api/reports/templates</c>                     — list accessible templates.</item>
///   <item><c>POST   /api/reports/templates</c>                     — create a template (owner = caller).</item>
///   <item><c>PUT    /api/reports/templates/{sqid}</c>              — owner-only update.</item>
///   <item><c>DELETE /api/reports/templates/{sqid}</c>              — owner-only soft delete.</item>
///   <item><c>POST   /api/reports/templates/{sqid}/run?skip=&amp;take=</c> — execute the template; paged result.</item>
///   <item><c>GET    /api/reports/templates/{sqid}/export?format=…</c> — render the result as a CSV/XLSX/PDF file.</item>
/// </list>
/// </para>
/// <para>
/// <b>Authorisation.</b> The list / run / export endpoints require
/// <see cref="AuthorizationComposition.CnasUser"/>; the create / update / delete
/// endpoints require <see cref="AuthorizationComposition.CnasAdmin"/> because
/// authoring an ad-hoc report grants the author execute-time access to any column
/// in the registry schema — a higher-privilege boundary than running a vetted
/// template.
/// </para>
/// <para>
/// <b>Sqid convention.</b> The <c>{sqid}</c> route segment is a Sqid-encoded id
/// per CLAUDE.md RULE 3. Malformed values surface as
/// <see cref="ErrorCodes.InvalidSqid"/> → 400.
/// </para>
/// </remarks>
/// <param name="templates">Underlying template service.</param>
/// <param name="engine">Underlying report engine.</param>
/// <param name="sqids">Sqid encoder/decoder for route ids.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasUser)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/reports/templates")]
public sealed class ReportTemplatesController(
    IReportTemplateService templates,
    IReportEngine engine,
    ISqidService sqids) : ControllerBase
{
    private readonly IReportTemplateService _templates = templates;
    private readonly IReportEngine _engine = engine;
    private readonly ISqidService _sqids = sqids;

    /// <summary>Lists every template the caller can access — own rows plus shared rows.</summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the list.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ReportTemplateDto>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var items = await _templates.ListAccessibleAsync(cancellationToken).ConfigureAwait(false);
        return Ok(items);
    }

    /// <summary>Persists a new template owned by the authenticated caller.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>201 with the new template; 400 / 409 on validation / code conflict.</returns>
    [HttpPost]
    [Authorize(Policy = AuthorizationComposition.CnasAdmin)]
    [Consumes("application/json")]
    public async Task<IActionResult> CreateAsync(
        [FromBody] ReportTemplateCreateDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _templates.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return MapFailureBare(result.ErrorCode, result.ErrorMessage);
        }
        return CreatedAtAction(nameof(ListAsync), new { }, result.Value);
    }

    /// <summary>Updates every mutable field on a template; owner only.</summary>
    /// <param name="sqid">Sqid-encoded id of the template.</param>
    /// <param name="input">Update payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the refreshed DTO; 400 / 403 / 404 on failure.</returns>
    [HttpPut("{sqid}")]
    [Authorize(Policy = AuthorizationComposition.CnasAdmin)]
    [Consumes("application/json")]
    public async Task<IActionResult> UpdateAsync(
        [FromRoute] string sqid,
        [FromBody] ReportTemplateUpdateDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureBare(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var result = await _templates.UpdateAsync(decoded.Value, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Soft-deletes a template (flips <c>IsActive=false</c>); owner only.</summary>
    /// <param name="sqid">Sqid-encoded id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 403 / 404 on failure.</returns>
    [HttpDelete("{sqid}")]
    [Authorize(Policy = AuthorizationComposition.CnasAdmin)]
    public async Task<IActionResult> DeleteAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureBare(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var result = await _templates.DeleteAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Executes a template and returns a single page of results.</summary>
    /// <param name="sqid">Sqid-encoded id.</param>
    /// <param name="skip">Rows to skip (≥ 0).</param>
    /// <param name="take">Page size; clamped server-side.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the executed page; 4xx on failure.</returns>
    [HttpPost("{sqid}/run")]
    public async Task<IActionResult> RunAsync(
        [FromRoute] string sqid,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureBare(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var result = await _engine.RunAsync(decoded.Value, skip, take, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Executes a template and renders the full result set as a downloadable file
    /// (CSV / XLSX / PDF).
    /// </summary>
    /// <param name="sqid">Sqid-encoded id.</param>
    /// <param name="format">Output format; default <c>csv</c>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the file bytes; 4xx on failure; 501 when format is unsupported.</returns>
    [HttpGet("{sqid}/export")]
    public async Task<IActionResult> ExportAsync(
        [FromRoute] string sqid,
        [FromQuery] ExportFormat format = ExportFormat.Csv,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureBare(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var result = await _engine.ExportAsync(decoded.Value, format, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return MapFailureBare(result.ErrorCode, result.ErrorMessage);
        }
        var contentType = ContentTypeFor(format);
        var fileName = $"report-{sqid}{ExtensionFor(format)}";
        return File(result.Value, contentType, fileName);
    }

    /// <summary>Maps a non-generic <see cref="Result"/> failure to a ProblemDetails action result.</summary>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>The mapped ProblemDetails / NotFound action result.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
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
        ErrorCodes.ConcurrencyConflict => StatusCodes.Status409Conflict,
        ErrorCodes.QueryTooBroad => StatusCodes.Status422UnprocessableEntity,
        ErrorCodes.ExportTooLarge => StatusCodes.Status422UnprocessableEntity,
        ErrorCodes.ExportFormatNotSupported => StatusCodes.Status501NotImplemented,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        ErrorCodes.QbeFieldNotQueryable => StatusCodes.Status400BadRequest,
        ErrorCodes.QbeOperatorNotSupported => StatusCodes.Status400BadRequest,
        ErrorCodes.QbeRegistryUnknown => StatusCodes.Status400BadRequest,
        ErrorCodes.QbeValueInvalid => StatusCodes.Status400BadRequest,
        ErrorCodes.QbeInvalidCombinator => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };

    /// <summary>MIME-type lookup for the export response.</summary>
    /// <param name="format">Requested format.</param>
    /// <returns>The canonical MIME type.</returns>
    private static string ContentTypeFor(ExportFormat format) => format switch
    {
        ExportFormat.Csv => "text/csv",
        ExportFormat.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ExportFormat.Pdf => "application/pdf",
        _ => "application/octet-stream",
    };

    /// <summary>File-extension lookup for the export response.</summary>
    /// <param name="format">Requested format.</param>
    /// <returns>Dotted extension (e.g. <c>.csv</c>).</returns>
    private static string ExtensionFor(ExportFormat format) => format switch
    {
        ExportFormat.Csv => ".csv",
        ExportFormat.Xlsx => ".xlsx",
        ExportFormat.Pdf => ".pdf",
        _ => ".bin",
    };
}
