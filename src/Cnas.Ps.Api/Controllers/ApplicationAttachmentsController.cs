using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Applications;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0322 / TOR UI 014 — REST surface for the first-class
/// <c>ApplicationAttachment</c> entity. Application-scoped routes
/// (attach / list) live under <c>/api/applications/{sqid}/attachments</c>;
/// row-scoped routes (get / remove / scan-result) live under
/// <c>/api/attachments/{sqid}/...</c>. All ids are Sqid-encoded per CLAUDE.md RULE 3.
/// </summary>
/// <param name="svc">Underlying attachment-service façade.</param>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
public sealed class ApplicationAttachmentsController(IApplicationAttachmentService svc)
    : ControllerBase
{
    private readonly IApplicationAttachmentService _svc = svc;

    /// <summary>Creates a new attachment-link row for the supplied application.</summary>
    /// <param name="applicationSqid">Sqid-encoded id of the parent application.</param>
    /// <param name="input">Attach payload (document id, category, mandatory snapshot, notes).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 201 with the persisted DTO, or a Problem response on failure
    /// (400 validation / 404 not-found / 409 conflict).
    /// </returns>
    [HttpPost("api/applications/{applicationSqid}/attachments")]
    public async Task<ActionResult<ApplicationAttachmentDto>> AttachAsync(
        [FromRoute] string applicationSqid,
        [FromBody] ApplicationAttachInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var result = await _svc.AttachAsync(applicationSqid, input, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return CreatedAtAction(nameof(GetAsync), new { attachmentSqid = result.Value!.Id }, result.Value);
        }
        return MapFailureGeneric<ApplicationAttachmentDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Paged listing of attachment-link rows for one application, ordered
    /// <c>AttachedAtUtc DESC</c>. Removed rows are excluded by default.
    /// </summary>
    /// <param name="applicationSqid">Sqid-encoded id of the parent application.</param>
    /// <param name="category">Optional category-enum-name filter.</param>
    /// <param name="virusScanStatus">Optional virus-scan-status filter.</param>
    /// <param name="includeRemoved">When true, includes removed rows.</param>
    /// <param name="skip">0-based offset (≥ 0).</param>
    /// <param name="take">Page size, clamped to <c>1..200</c>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the page or a Problem response on failure.</returns>
    [HttpGet("api/applications/{applicationSqid}/attachments")]
    public async Task<ActionResult<ApplicationAttachmentPageDto>> ListByApplicationAsync(
        [FromRoute] string applicationSqid,
        [FromQuery] string? category = null,
        [FromQuery] string? virusScanStatus = null,
        [FromQuery] bool includeRemoved = false,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken cancellationToken = default)
    {
        var filter = new ApplicationAttachmentFilterDto(category, virusScanStatus, includeRemoved, skip, take);
        var result = await _svc.ListByApplicationAsync(applicationSqid, filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<ApplicationAttachmentPageDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Loads one attachment-link row by Sqid id.</summary>
    /// <param name="attachmentSqid">Sqid-encoded id of the attachment row.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the DTO or a Problem response on failure.</returns>
    [HttpGet("api/attachments/{attachmentSqid}")]
    public async Task<ActionResult<ApplicationAttachmentDto>> GetAsync(
        [FromRoute] string attachmentSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.GetByIdAsync(attachmentSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<ApplicationAttachmentDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Soft-removes an attachment-link row. The underlying document is untouched.</summary>
    /// <param name="attachmentSqid">Sqid-encoded id of the attachment row.</param>
    /// <param name="input">Reason payload (3..500 chars).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 No Content or a Problem response on failure.</returns>
    [HttpPost("api/attachments/{attachmentSqid}/remove")]
    public async Task<IActionResult> RemoveAsync(
        [FromRoute] string attachmentSqid,
        [FromBody] ApplicationAttachmentReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var result = await _svc.RemoveAsync(attachmentSqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Records the outcome of a virus scan. Gated to <c>cnas-tech-admin</c>
    /// because only the scan worker should be able to flip this lifecycle
    /// state — the citizen-facing surface never calls it.
    /// </summary>
    /// <param name="attachmentSqid">Sqid-encoded id of the attachment row.</param>
    /// <param name="input">Scan-result payload (status, scanner, optional notes).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 No Content or a Problem response on failure.</returns>
    [HttpPost("api/attachments/{attachmentSqid}/scan-result")]
    [Authorize(Policy = AuthorizationComposition.CnasTechAdmin)]
    public async Task<IActionResult> RecordScanResultAsync(
        [FromRoute] string attachmentSqid,
        [FromBody] ApplicationAttachmentScanResultInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var result = await _svc.RecordVirusScanResultAsync(attachmentSqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a <see cref="Result{T}"/> failure to an <see cref="ActionResult{T}"/>.</summary>
    /// <typeparam name="T">DTO type the action would have returned on success.</typeparam>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>An appropriate Problem / NotFound / Conflict response.</returns>
    private ActionResult<T> MapFailureGeneric<T>(string? code, string? message)
        => code switch
        {
            ErrorCodes.NotFound => NotFound(),
            ErrorCodes.Conflict => Conflict(new { error = code, message }),
            _ => Problem(message, statusCode: StatusCodes.Status400BadRequest),
        };

    /// <summary>Maps a non-generic <see cref="Result"/> failure to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>An appropriate Problem / NotFound / Conflict response.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
        => code switch
        {
            ErrorCodes.NotFound => NotFound(),
            ErrorCodes.Conflict => Conflict(new { error = code, message }),
            _ => Problem(message, statusCode: StatusCodes.Status400BadRequest),
        };
}
