using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// UC08 — Examination REST surface. Hosts the examiner-facing endpoints for verdict
/// recording, draft document generation, submission for approval, and outright refusal.
/// All endpoints require the <see cref="AuthorizationComposition.CnasUser"/> policy
/// (any authenticated CNAS staff role).
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasUser)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/examination")]
public sealed class ExaminationController(IDocumentExaminationService svc) : ControllerBase
{
    private readonly IDocumentExaminationService _svc = svc;

    /// <summary>
    /// UC08.02 — Records the examiner's verdict on a single attached document. The
    /// document is identified by its Sqid id; the verdict string must parse to a
    /// <see cref="ExaminationVerdict"/> value.
    /// </summary>
    /// <param name="id">Sqid id of the document.</param>
    /// <param name="body">Verdict + optional note.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("documents/{id}/verdict")]
    public async Task<IActionResult> RecordVerdictAsync(
        string id,
        [FromBody] VerdictRequest body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!Enum.TryParse<ExaminationVerdict>(body.Verdict, ignoreCase: true, out var parsed))
        {
            return Problem("Unknown verdict value.", statusCode: 400);
        }

        var result = await _svc.RecordVerdictAsync(id, parsed, body.Note, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok()
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// UC08.04 — Triggers system auto-generation of the Fișa de calcul + Decizia drafts.
    /// Returns both Sqid identifiers so the UI can immediately preview them.
    /// </summary>
    /// <param name="id">Sqid id of the dossier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("dossiers/{id}/generate-drafts")]
    public async Task<ActionResult<DraftDocumentsResult>> GenerateDraftsAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.GenerateDraftsAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// UC08.06 — Forwards the dossier to the șef-direcție inbox for final approval (UC10).
    /// Only the assigned examiner can call this endpoint.
    /// </summary>
    /// <param name="id">Sqid id of the dossier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("dossiers/{id}/submit-for-approval")]
    public async Task<IActionResult> SubmitForApprovalAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.SubmitForApprovalAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok()
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// UC08.06 (alt branch) — Examiner rejects the application outright without sending it
    /// for approval. The reason is mandatory; missing / empty bodies return 400.
    /// </summary>
    /// <param name="id">Sqid id of the dossier.</param>
    /// <param name="body">Reason payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("dossiers/{id}/refuse")]
    public async Task<IActionResult> RefuseAsync(
        string id,
        [FromBody] RefuseRequest body,
        CancellationToken cancellationToken = default)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Reason))
        {
            return Problem("Reason is required.", statusCode: 400);
        }

        var result = await _svc.RefuseAsync(id, body.Reason, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok()
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// UC08.05 (R0573) — Examiner emits a brand-new decision document anchored to
    /// the dossier. The examiner picks the Annex 7 template by its stable
    /// kebab-case code; the service verifies the dossier is still in an editable
    /// state, that the supplied code matches a registered template, then renders
    /// + persists the new document, audits the event, and notifies the
    /// solicitant.
    /// </summary>
    /// <param name="sqid">Sqid id of the dossier (examination anchor).</param>
    /// <param name="body">Template code + optional note + optional override amount.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>200 OK</c> with an <see cref="EmittedDecisionDto"/> body on success;
    /// <c>400</c> on validation / Sqid failures; <c>403</c> when the caller is
    /// not the assigned examiner; <c>404</c> when the dossier or template is
    /// missing; <c>409</c> when the examination is no longer editable.
    /// </returns>
    [HttpPost("dossiers/{sqid}/emit-decision")]
    public async Task<ActionResult<EmittedDecisionDto>> EmitNewDecisionAsync(
        string sqid,
        [FromBody] EmitNewDecisionInputDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        // Boundary validation. The validator runs against the wire DTO before
        // any service-layer state is touched; any failure surfaces as 400.
        var validator = new Cnas.Ps.Application.Validators.EmitNewDecisionInputValidator();
        var validation = validator.Validate(body);
        if (!validation.IsValid)
        {
            return Problem(
                detail: string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: 400);
        }

        var result = await _svc.EmitNewDecisionAsync(sqid, body, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a service-layer failure to the matching HTTP status.</summary>
    /// <param name="errorCode">Stable error code from <c>ErrorCodes</c>.</param>
    /// <param name="errorMessage">Human-readable message.</param>
    private ObjectResult MapFailure(string? errorCode, string? errorMessage)
    {
        var status = errorCode switch
        {
            "NOT_FOUND" => 404,
            "DOCUMENT.TEMPLATE_NOT_FOUND" => 404,
            "FORBIDDEN" => 403,
            "UNAUTHORIZED" => 401,
            "EXAMINATION.NOT_EDITABLE" => 409,
            _ => 400,
        };
        return Problem(errorMessage, statusCode: status);
    }
}

/// <summary>Request payload for <see cref="ExaminationController.RecordVerdictAsync"/>.</summary>
/// <param name="Verdict">String form of <see cref="ExaminationVerdict"/> (case-insensitive).</param>
/// <param name="Note">Optional free-text note.</param>
public sealed record VerdictRequest(string Verdict, string? Note);

/// <summary>Request payload for <see cref="ExaminationController.RefuseAsync"/>.</summary>
/// <param name="Reason">Mandatory reason recorded on the audit log + sent to the solicitant.</param>
public sealed record RefuseRequest(string Reason);
