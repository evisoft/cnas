using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// Annex 1 — <c>Plătitori de contribuții</c> registry REST surface. All endpoints require
/// authentication and a CNAS-staff role. Identifiers crossing the wire are Sqid-encoded
/// per CLAUDE.md RULE 3.
/// </summary>
/// <param name="svc">Underlying registry façade.</param>
/// <param name="clock">UTC clock — required so <c>IsInsuredAsync</c> can supply a default
/// "as of now" value without calling <see cref="DateTime.UtcNow"/> directly (CLAUDE.md §6.x —
/// UTC Everywhere; enforced by <c>TimeProviderUsageTests</c>).</param>
/// <param name="sqids">Sqid encoder/decoder for the R0305 BP route parameters (CLAUDE.md RULE 3).</param>
/// <param name="exportSelector">
/// R0610 / TOR CF 12.01 — iter 125 — universal report-export pipeline. Used by
/// <see cref="SearchAsync"/> when the caller passes <c>format=xlsx|pdf</c>; the
/// CSV path stays JSON-paged for backward compatibility.
/// </param>
[ApiController]
[Authorize(Roles = "cnas-user,cnas-admin")]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/contributors")]
public sealed class ContributorsController(
    IContributorService svc,
    ICnasTimeProvider clock,
    ISqidService sqids,
    IReportExportSelector exportSelector) : ControllerBase
{
    private readonly IContributorService _svc = svc;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ISqidService _sqids = sqids;
    private readonly IReportExportSelector _exportSelector = exportSelector;

    /// <summary>Register a new contributor. Returns 201 with a Location header on success.</summary>
    /// <param name="input">Registration payload (IDNO + denumire + optional classifier codes).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>201 with the new Sqid id, or a Problem response on failure.</returns>
    [HttpPost]
    public async Task<ActionResult<string>> RegisterAsync(
        [FromBody] ContributorRegistrationInput input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.RegisterAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetAsync), new { id = result.Value }, result.Value)
            : MapFailureGeneric<string>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Load a single contributor by its Sqid id.</summary>
    /// <param name="id">Sqid-encoded contributor id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the contributor, 404 when not found, 400 on invalid id.</returns>
    [HttpGet("{id}")]
    public async Task<ActionResult<ContributorOutput>> GetAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<ContributorOutput>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Load a single contributor by its 13-digit IDNO.</summary>
    /// <param name="idno">Candidate IDNO (validated at the service boundary).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the contributor, 404 when not found, 400 on invalid IDNO.</returns>
    [HttpGet("by-idno/{idno}")]
    public async Task<ActionResult<ContributorOutput>> GetByIdnoAsync(
        string idno,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.GetByIdnoAsync(idno, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<ContributorOutput>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Paged search by substring of Denumire or IDNO.</summary>
    /// <param name="q">Optional substring filter (case-insensitive).</param>
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
    public async Task<ActionResult<PagedResult<ContributorListItem>>> SearchAsync(
        [FromQuery] string? q = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] ReportExportFormat? format = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.SearchAsync(q, new PageRequest(page, pageSize), cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return MapFailureGeneric<PagedResult<ContributorListItem>>(result.ErrorCode, result.ErrorMessage);
        }

        // R0610 — default (no format / Csv) keeps the legacy JSON envelope so
        // existing SPA consumers do not need to change. Xlsx/Pdf route through
        // the universal exporter pipeline; the response becomes a file.
        if (format is null or ReportExportFormat.Csv)
        {
            return Ok(result.Value);
        }

        var projection = RegistryExportProjection.ForContributors(
            title: "Contributors",
            items: result.Value.Items);

        var exportResult = await _exportSelector
            .ExportAsync(format.Value, projection, cancellationToken)
            .ConfigureAwait(false);
        if (exportResult.IsFailure)
        {
            return MapExportFailure<PagedResult<ContributorListItem>>(
                format.Value, exportResult.ErrorCode, exportResult.ErrorMessage);
        }

        var fileName = $"contributors-{_clock.UtcNow:yyyyMMdd}{exportResult.Value.FileExtension}";
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

    /// <summary>Flag a contributor as insolvent.</summary>
    /// <param name="id">Sqid-encoded contributor id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 No Content on success; failure mapped via <see cref="MapFailureBare"/>.</returns>
    [HttpPost("{id}/insolvent")]
    public async Task<IActionResult> MarkInsolventAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.MarkInsolventAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Clear a contributor's insolvent flag.</summary>
    /// <param name="id">Sqid-encoded contributor id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 No Content on success; failure mapped via <see cref="MapFailureBare"/>.</returns>
    [HttpPost("{id}/solvent")]
    public async Task<IActionResult> MarkSolventAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.MarkSolventAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Soft-delete (de-register) the contributor.</summary>
    /// <param name="id">Sqid-encoded contributor id.</param>
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

    // ─── R0305 — Annex 1 Business Process endpoints (BP 1.2/1.3/1.4/1.5/1.6/1.7/1.9) ───

    /// <summary>
    /// R0305 / BP 1.2 — update mutable primary attributes (Denumire + classifier codes).
    /// </summary>
    /// <param name="id">Sqid-encoded contributor id.</param>
    /// <param name="input">Updated attribute values.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with updated contributor, 404/409/400 on failure.</returns>
    [HttpPut("{id}/attributes")]
    public async Task<ActionResult<ContributorOutput>> UpdateAttributesAsync(
        string id,
        [FromBody] ContributorAttributesUpdateDto input,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(id);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<ContributorOutput>(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.UpdateAttributesAsync(decoded.Value, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<ContributorOutput>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0305 / BP 1.3 — administratively deactivate the contributor.</summary>
    /// <param name="id">Sqid-encoded contributor id.</param>
    /// <param name="input">Deactivation reason (3..500 chars).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success, 404/409/400 on failure.</returns>
    [HttpPost("{id}/deactivate")]
    public async Task<IActionResult> DeactivateBpAsync(
        string id,
        [FromBody] ContributorDeactivationInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(id);
        if (decoded.IsFailure)
        {
            return MapFailureBare(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.DeactivateAsync(decoded.Value, input.Reason, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0305 / BP 1.4 — reactivate a previously-deactivated contributor.</summary>
    /// <param name="id">Sqid-encoded contributor id.</param>
    /// <param name="input">Reactivation reason (3..500 chars).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success, 404/409/400 on failure.</returns>
    [HttpPost("{id}/reactivate")]
    public async Task<IActionResult> ReactivateAsync(
        string id,
        [FromBody] ContributorReactivationInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(id);
        if (decoded.IsFailure)
        {
            return MapFailureBare(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.ReactivateAsync(decoded.Value, input.Reason, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0305 / BP 1.5 — merge a duplicate contributor into a survivor.</summary>
    /// <param name="duplicateSqid">Sqid-encoded duplicate contributor id.</param>
    /// <param name="survivorSqid">Sqid-encoded survivor contributor id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success, 404/403/400 on failure.</returns>
    [HttpPost("{duplicateSqid}/merge-into/{survivorSqid}")]
    public async Task<IActionResult> MergeDuplicatesAsync(
        string duplicateSqid,
        string survivorSqid,
        CancellationToken cancellationToken = default)
    {
        var duplicate = _sqids.TryDecode(duplicateSqid);
        if (duplicate.IsFailure)
        {
            return MapFailureBare(duplicate.ErrorCode, duplicate.ErrorMessage);
        }
        var survivor = _sqids.TryDecode(survivorSqid);
        if (survivor.IsFailure)
        {
            return MapFailureBare(survivor.ErrorCode, survivor.ErrorMessage);
        }

        var result = await _svc.MergeDuplicatesAsync(duplicate.Value, survivor.Value, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0305 / BP 1.6 — placeholder for split operation (501 Not Implemented).</summary>
    /// <param name="id">Sqid-encoded contributor id.</param>
    /// <param name="input">Split rationale.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>501 Not Implemented — deferred-by-design.</returns>
    [HttpPost("{id}/split")]
    public async Task<IActionResult> SplitAsync(
        string id,
        [FromBody] ContributorSplitInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(id);
        if (decoded.IsFailure)
        {
            return MapFailureBare(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.SplitAsync(decoded.Value, input, cancellationToken).ConfigureAwait(false);
        // BP 1.6 is deferred-by-design — the service always returns NotImplemented; surface
        // as HTTP 501 so clients can hide the corresponding UI affordance.
        return result.IsSuccess
            ? NoContent()
            : Problem(result.ErrorMessage, statusCode: StatusCodes.Status501NotImplemented);
    }

    /// <summary>R0305 / BP 1.7 — record an admin-only field-level correction (audit-only).</summary>
    /// <param name="id">Sqid-encoded contributor id.</param>
    /// <param name="input">Correction metadata (field name + hashed before/after + reason).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success, 403/404/400 on failure.</returns>
    [HttpPost("{id}/admin-correct")]
    [Authorize(Roles = "cnas-admin")]
    public async Task<IActionResult> AdminCorrectAsync(
        string id,
        [FromBody] ContributorAdminCorrectionInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(id);
        if (decoded.IsFailure)
        {
            return MapFailureBare(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.AdminCorrectAsync(
            decoded.Value,
            input.FieldName,
            input.OldValueHash,
            input.NewValueHash,
            input.Reason,
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0305 / BP 1.9 — mark contributor as deceased (natural) or dissolved (legal).</summary>
    /// <param name="id">Sqid-encoded contributor id.</param>
    /// <param name="input">Effective date carrier.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success, 404/409/400 on failure.</returns>
    [HttpPost("{id}/mark-deceased-or-dissolved")]
    public async Task<IActionResult> MarkDeceasedOrDissolvedAsync(
        string id,
        [FromBody] ContributorMarkDeceasedInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(id);
        if (decoded.IsFailure)
        {
            return MapFailureBare(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.MarkDeceasedOrDissolvedAsync(
            decoded.Value,
            input.EffectiveDate,
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Is the contributor with this IDNO insured at the given UTC moment?</summary>
    /// <param name="idno">Candidate IDNO.</param>
    /// <param name="atUtc">Optional as-of moment; defaults to <see cref="ICnasTimeProvider.UtcNow"/> when omitted.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the boolean answer, 400 on invalid IDNO.</returns>
    [HttpGet("is-insured/{idno}")]
    public async Task<ActionResult<IsInsuredResult>> IsInsuredAsync(
        string idno,
        [FromQuery] DateTime? atUtc = null,
        CancellationToken cancellationToken = default)
    {
        // Default to the clock-provided UTC instant per the UTC-Everywhere rule. The
        // architecture test (TimeProviderUsageTests) forbids DateTime.UtcNow anywhere
        // outside the system clock implementation.
        var asOf = atUtc ?? _clock.UtcNow;
        var result = await _svc.IsInsuredAsync(idno, asOf, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<IsInsuredResult>(result.ErrorCode, result.ErrorMessage);
    }

    // ─── Failure mapping helpers ───
    // Mirrors the pattern from ApplicationsController but adds NotFound / Conflict /
    // InvalidSqid / InvalidIdno mapping so each ErrorCode surfaces with the correct
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
    /// <returns>404 for NotFound, 409 for Conflict, 400 for InvalidSqid/InvalidIdno/other.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Conflict => StatusCodes.Status409Conflict,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.NotImplemented => StatusCodes.Status501NotImplemented,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidIdno => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
