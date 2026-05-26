using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// Annex 2 — <c>Persoane asigurate</c> registry REST surface. All endpoints require
/// authentication and a CNAS-staff role. Identifiers crossing the wire are Sqid-encoded
/// per CLAUDE.md RULE 3.
/// </summary>
/// <param name="svc">Underlying registry façade.</param>
/// <param name="exportSelector">
/// R0610 / TOR CF 12.01 — iter 125 — universal report-export pipeline. Used by
/// <see cref="SearchAsync"/> when the caller passes <c>format=xlsx|pdf</c>; the
/// CSV path stays JSON-paged for backward compatibility.
/// </param>
/// <param name="clock">
/// R0610 / iter 125 — UTC clock used to stamp the export filename. Required so the
/// controller does not call <see cref="DateTime.UtcNow"/> directly (CLAUDE.md
/// "UTC Everywhere"; enforced by <c>TimeProviderUsageTests</c>).
/// </param>
[ApiController]
[Authorize(Roles = "cnas-user,cnas-admin")]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/insured-persons")]
public sealed class InsuredPersonsController(
    IInsuredPersonService svc,
    IReportExportSelector exportSelector,
    ICnasTimeProvider clock) : ControllerBase
{
    private readonly IInsuredPersonService _svc = svc;
    private readonly IReportExportSelector _exportSelector = exportSelector;
    private readonly ICnasTimeProvider _clock = clock;

    /// <summary>Register a new insured person. Returns 201 with a Location header on success.</summary>
    /// <param name="input">Registration payload (IDNP + name fields + birth date).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>201 with the new Sqid id, or a Problem response on failure.</returns>
    [HttpPost]
    public async Task<ActionResult<string>> RegisterAsync(
        [FromBody] InsuredPersonRegistrationInput input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.RegisterAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetAsync), new { id = result.Value }, result.Value)
            : MapFailureGeneric<string>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Load a single insured person by its Sqid id.</summary>
    /// <param name="id">Sqid-encoded insured-person id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the record, 404 when not found, 400 on invalid id.</returns>
    [HttpGet("{id}")]
    public async Task<ActionResult<InsuredPersonOutput>> GetAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<InsuredPersonOutput>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Load a single insured person by its 13-digit IDNP.</summary>
    /// <param name="idnp">Candidate IDNP (validated at the service boundary).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the record, 404 when not found, 400 on invalid IDNP.</returns>
    [HttpGet("by-idnp/{idnp}")]
    public async Task<ActionResult<InsuredPersonOutput>> GetByIdnpAsync(
        string idnp,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.GetByIdnpAsync(idnp, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<InsuredPersonOutput>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Paged search by substring of any name component or IDNO.</summary>
    /// <param name="query">Optional substring filter (case-insensitive).</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Items per page (service clamps to [1, 200]).</param>
    /// <param name="format">
    /// R0610 / TOR CF 12.01 — iter 125 — optional export format. When omitted
    /// (or <see cref="ReportExportFormat.Csv"/>) the endpoint returns the
    /// legacy paged JSON envelope. When set to <c>Xlsx</c> or <c>Pdf</c> the
    /// endpoint projects the current page through
    /// <see cref="IReportExportSelector"/> and streams the rendered file back
    /// as a <see cref="FileContentResult"/>.
    /// </param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with a paged list of registry rows (default JSON path), or 200 with
    /// a <see cref="FileContentResult"/> when an export format is requested.
    /// 501 when the requested format has no registered exporter.
    /// </returns>
    [HttpGet]
    public async Task<ActionResult<PagedResult<InsuredPersonListItem>>> SearchAsync(
        [FromQuery] string? query = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] ReportExportFormat? format = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.SearchAsync(query, new PageRequest(page, pageSize), cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return MapFailureGeneric<PagedResult<InsuredPersonListItem>>(result.ErrorCode, result.ErrorMessage);
        }

        // R0610 — JSON path stays the default; only xlsx/pdf flip to file streaming.
        if (format is null or ReportExportFormat.Csv)
        {
            return Ok(result.Value);
        }

        var projection = RegistryExportProjection.ForInsuredPersons(
            title: "InsuredPersons",
            items: result.Value.Items);

        var exportResult = await _exportSelector
            .ExportAsync(format.Value, projection, cancellationToken)
            .ConfigureAwait(false);
        if (exportResult.IsFailure)
        {
            return MapExportFailure<PagedResult<InsuredPersonListItem>>(
                format.Value, exportResult.ErrorCode, exportResult.ErrorMessage);
        }

        // Filename embeds today's UTC stamp so repeated exports are distinguishable
        // on the user's filesystem. We use the same yyyyMMdd format the ReportsController
        // export endpoint uses for consistency.
        var fileName = $"insured-persons-{_clock.UtcNow:yyyyMMdd}{exportResult.Value.FileExtension}";
        return File(exportResult.Value.Bytes, exportResult.Value.ContentType, fileName);
    }

    /// <summary>
    /// R0610 — translates an <see cref="IReportExportSelector"/> failure into
    /// the right HTTP status. Unknown-format failures surface as 501; everything
    /// else falls back to the standard <see cref="MapFailureGeneric{T}"/> path.
    /// </summary>
    /// <typeparam name="T">The DTO type the action would have returned on success.</typeparam>
    /// <param name="format">The requested export format (echoed in the ProblemDetails for diagnostics).</param>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>501 ProblemDetails for unsupported formats; otherwise the standard mapping.</returns>
    private ActionResult<T> MapExportFailure<T>(ReportExportFormat format, string? code, string? message)
    {
        if (code == ErrorCodes.ExportFormatNotSupported || code == ErrorCodes.ExportDocxNotAvailable)
        {
            return StatusCode(StatusCodes.Status501NotImplemented,
                new ProblemDetails
                {
                    Title = "Export format not available",
                    Detail = message,
                    Status = StatusCodes.Status501NotImplemented,
                    Extensions = { ["format"] = format.ToString() },
                });
        }
        return MapFailureGeneric<T>(code, message);
    }

    /// <summary>Request payload for the "mark deceased" endpoint.</summary>
    /// <param name="DateOfDeath">Date of death sourced from eCMND.</param>
    public sealed record MarkDeceasedRequest(DateOnly DateOfDeath);

    /// <summary>Record a deceased flag against an insured person.</summary>
    /// <param name="id">Sqid-encoded insured-person id.</param>
    /// <param name="body">Request body carrying the date of death.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 No Content on success; failure mapped via <see cref="MapFailureBare"/>.</returns>
    [HttpPost("{id}/deceased")]
    public async Task<IActionResult> MarkDeceasedAsync(
        string id,
        [FromBody] MarkDeceasedRequest body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await _svc.MarkDeceasedAsync(id, body.DateOfDeath, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Soft-delete an insured person.</summary>
    /// <param name="id">Sqid-encoded insured-person id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 No Content on success; failure mapped via <see cref="MapFailureBare"/>.</returns>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeactivateAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.DeactivateAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    // ─── Failure mapping helpers ───
    // Mirrors the pattern from ContributorsController but adds NotFound / Conflict /
    // InvalidSqid / InvalidIdnp mapping so each ErrorCode surfaces with the correct
    // HTTP status to API clients.

    /// <summary>Maps a <see cref="Result{T}"/> failure to an <see cref="ActionResult{T}"/>.</summary>
    /// <typeparam name="T">The DTO type the action would have returned.</typeparam>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 409 / 400 ProblemDetails as appropriate.</returns>
    private ActionResult<T> MapFailureGeneric<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Maps a non-generic <see cref="Result"/> failure to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 409 / 400 ProblemDetails as appropriate.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>404 for NotFound, 409 for Conflict, 400 for InvalidSqid/InvalidIdnp/other.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Conflict => StatusCodes.Status409Conflict,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidIdnp => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
