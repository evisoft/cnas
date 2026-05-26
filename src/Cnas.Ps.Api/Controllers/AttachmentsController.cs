using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Attachments;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0227 / TOR UI 014 — REST surface for the reusable file-attachment widget. Drives
/// <see cref="IAttachmentService"/>: upload / list / get / download / archive / delete.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST /api/attachments</c> — upload a new attachment (201 + Location).</item>
///   <item><c>GET  /api/attachments?ownerType=X&amp;ownerSqid=Y</c> — list by owner.</item>
///   <item><c>GET  /api/attachments/{attachmentSqid}</c> — fetch row metadata (NOT the bytes).</item>
///   <item><c>GET  /api/attachments/{attachmentSqid}/download</c> — fetch bytes (FileResult).</item>
///   <item><c>POST /api/attachments/{attachmentSqid}/archive</c> — soft-archive.</item>
///   <item><c>DELETE /api/attachments/{attachmentSqid}</c> — soft-delete.</item>
/// </list>
/// </para>
/// <para>
/// <b>Error-code → HTTP status mapping.</b>
/// <see cref="ErrorCodes.NotFound"/> → 404,
/// <see cref="ErrorCodes.Forbidden"/> → 403,
/// <see cref="ErrorCodes.Unauthorized"/> → 401,
/// <see cref="ErrorCodes.FileTooLarge"/> → 413 PayloadTooLarge,
/// <see cref="ErrorCodes.FileTypeMismatch"/> → 400,
/// <see cref="ErrorCodes.FileUnavailable"/> → 410 Gone,
/// <see cref="ErrorCodes.ValidationFailed"/> / <see cref="ErrorCodes.InvalidSqid"/> → 400.
/// </para>
/// </remarks>
/// <param name="attachments">Underlying attachment service.</param>
/// <param name="uploadValidator">FluentValidation rule set for <see cref="AttachmentUploadDto"/>.</param>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/attachments")]
public sealed class AttachmentsController(
    IAttachmentService attachments,
    IValidator<AttachmentUploadDto> uploadValidator) : ControllerBase
{
    private readonly IAttachmentService _attachments = attachments;
    private readonly IValidator<AttachmentUploadDto> _uploadValidator = uploadValidator;

    /// <summary>
    /// Persists a new attachment. The caller must be authenticated. The owner reference,
    /// declared filename, category, and (optional) sensitivity label are validated
    /// against the frozen allow-lists before the underlying service is invoked.
    /// </summary>
    /// <param name="input">Upload payload (required body).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>201 with the persisted (or dedup-returned) DTO; per the class-level table on failure.</returns>
    [HttpPost]
    [EnableRateLimiting(RateLimitingPolicies.Upload)]
    [Consumes("application/json")]
    public async Task<IActionResult> UploadAsync(
        [FromBody] AttachmentUploadDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _uploadValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                detail: string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _attachments.UploadAsync(input, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return MapFailure(result.ErrorCode, result.ErrorMessage);
        }

        return CreatedAtAction(
            actionName: nameof(GetAsync),
            routeValues: new { attachmentSqid = result.Value.Id },
            value: result.Value);
    }

    /// <summary>
    /// Lists every visible (active, non-archived) attachment for the supplied owner.
    /// </summary>
    /// <param name="ownerType">Stable CLR-type name of the owning entity.</param>
    /// <param name="ownerSqid">Sqid-encoded id of the owning entity.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the (possibly empty) list, or a stable failure status.</returns>
    [HttpGet]
    public async Task<IActionResult> ListAsync(
        [FromQuery] string ownerType,
        [FromQuery] string ownerSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _attachments.ListAsync(ownerType, ownerSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Returns the metadata for a single attachment — NOT the bytes. Use the
    /// <c>{sqid}/download</c> endpoint when the bytes are needed.
    /// </summary>
    /// <param name="attachmentSqid">Sqid-encoded attachment id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the DTO, or a stable failure status.</returns>
    [HttpGet("{attachmentSqid}")]
    public async Task<IActionResult> GetAsync(
        [FromRoute] string attachmentSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _attachments.GetAsync(attachmentSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Streams the attachment bytes back to the caller. Maps to
    /// <c>ControllerBase.File(byte[], string, string)</c> with the row's MIME type and
    /// sanitised filename.
    /// </summary>
    /// <param name="attachmentSqid">Sqid-encoded attachment id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the file bytes, or a stable failure status.</returns>
    [HttpGet("{attachmentSqid}/download")]
    public async Task<IActionResult> DownloadAsync(
        [FromRoute] string attachmentSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _attachments.DownloadAsync(attachmentSqid, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return MapFailure(result.ErrorCode, result.ErrorMessage);
        }
        return File(result.Value.Bytes, result.Value.ContentType, result.Value.FileName);
    }

    /// <summary>
    /// Soft-archives the attachment — sets <c>IsArchived=true</c>.
    /// </summary>
    /// <param name="attachmentSqid">Sqid-encoded attachment id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success, stable failure status otherwise.</returns>
    [HttpPost("{attachmentSqid}/archive")]
    public async Task<IActionResult> ArchiveAsync(
        [FromRoute] string attachmentSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _attachments.ArchiveAsync(attachmentSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? NoContent()
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Soft-deletes the attachment — sets <c>IsActive=false</c>.
    /// </summary>
    /// <param name="attachmentSqid">Sqid-encoded attachment id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success, stable failure status otherwise.</returns>
    [HttpDelete("{attachmentSqid}")]
    public async Task<IActionResult> DeleteAsync(
        [FromRoute] string attachmentSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _attachments.DeleteAsync(attachmentSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? NoContent()
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a stable service-layer error code + message to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>Mapped ProblemDetails / NotFound result.</returns>
    private IActionResult MapFailure(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(detail: message, statusCode: status);
    }

    /// <summary>
    /// Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.
    /// </summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>HTTP status code.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.FileTooLarge => StatusCodes.Status413PayloadTooLarge,
        ErrorCodes.FileUnavailable => StatusCodes.Status410Gone,
        ErrorCodes.FileTypeMismatch => StatusCodes.Status400BadRequest,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
