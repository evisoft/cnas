using System.Security.Claims;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Interop.Batch;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers.Interop;

/// <summary>
/// R1710 / TOR INT 002 / Annex 4 — REST surface for the offline-batch
/// (file-based) equivalents of the synchronous Annex-4 endpoints. Every
/// route is gated by the <c>InteropClient</c> role.
/// </summary>
/// <remarks>
/// <para>
/// <b>ConsumerSubject is server-filled.</b> The controller overwrites any
/// <c>ConsumerSubject</c> the client supplied in the input DTO with the
/// authenticated subject extracted from the <c>sub</c> / NameIdentifier
/// claim. Cross-subject reads (e.g. one consumer probing another's batch)
/// are rejected with 404 — the lookup is scoped to the caller.
/// </para>
/// <para>
/// <b>Why a relative DownloadUrl.</b> The download endpoint lives on this
/// controller; the <see cref="OfflineBatchDownloadInfoDto"/> records the
/// path so consumers can follow it with a separate, authenticated call
/// that streams the CSV with the integrity-stamp headers.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "InteropClient")]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/interop/batch")]
public sealed class OfflineBatchController : ControllerBase
{
    private readonly IOfflineBatchSubmissionService _service;

    /// <summary>Constructs the controller.</summary>
    /// <param name="service">Submission façade.</param>
    public OfflineBatchController(IOfflineBatchSubmissionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <summary>
    /// <c>POST /api/interop/batch/submissions</c> — accepts a CSV upload
    /// and queues it for processing. Server-fills <c>ConsumerSubject</c>
    /// from the auth context, overwriting any client value.
    /// </summary>
    /// <param name="body">Submission input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>201 Created with the outbound projection.</returns>
    [HttpPost("submissions")]
    public async Task<ActionResult<OfflineBatchSubmissionDto>> SubmitAsync(
        [FromBody] OfflineBatchSubmissionInputDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var subject = RequireSubject();
        if (subject is null)
        {
            return Unauthorized();
        }
        // Server-fill ConsumerSubject — clients cannot influence this value.
        var sanitised = body with { ConsumerSubject = subject };
        var result = await _service.SubmitAsync(sanitised, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return MapFailure<OfflineBatchSubmissionDto>(result.ErrorCode!, result.ErrorMessage!);
        }
        return CreatedAtAction(
            nameof(GetByIdAsync),
            new { sqid = result.Value.Id },
            result.Value);
    }

    /// <summary>
    /// <c>POST /api/interop/batch/submissions/{sqid}/cancel</c> — cancels a
    /// Submitted / Queued submission belonging to the caller.
    /// </summary>
    /// <param name="sqid">Sqid-encoded submission id.</param>
    /// <param name="body">Cancellation rationale.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 / 400 / 404 / 409.</returns>
    [HttpPost("submissions/{sqid}/cancel")]
    public async Task<ActionResult<OfflineBatchSubmissionDto>> CancelAsync(
        string sqid,
        [FromBody] OfflineBatchReasonInputDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var subject = RequireSubject();
        if (subject is null) { return Unauthorized(); }

        // Pre-check ownership: a 404 for cross-subject lookups so the
        // existence of another consumer's batch never leaks.
        var existing = await _service.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (existing.IsFailure)
        {
            return MapFailure<OfflineBatchSubmissionDto>(existing.ErrorCode!, existing.ErrorMessage!);
        }
        if (!string.Equals(existing.Value.ConsumerSubject, subject, StringComparison.Ordinal))
        {
            return NotFound(new { error = ErrorCodes.NotFound, message = "Submission not found." });
        }

        var result = await _service.CancelAsync(sqid, body, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<OfflineBatchSubmissionDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// <c>GET /api/interop/batch/submissions/{sqid}</c> — single-row lookup.
    /// </summary>
    /// <param name="sqid">Sqid-encoded submission id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the projection; 404 when not the caller's batch.</returns>
    [HttpGet("submissions/{sqid}")]
    public async Task<ActionResult<OfflineBatchSubmissionDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var subject = RequireSubject();
        if (subject is null) { return Unauthorized(); }
        var result = await _service.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return MapFailure<OfflineBatchSubmissionDto>(result.ErrorCode!, result.ErrorMessage!);
        }
        if (!string.Equals(result.Value.ConsumerSubject, subject, StringComparison.Ordinal))
        {
            return NotFound(new { error = ErrorCodes.NotFound, message = "Submission not found." });
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// <c>GET /api/interop/batch/submissions/{sqid}/download</c> — streams the
    /// response CSV when the submission is Completed. Headers carry the
    /// integrity hash + HMAC signature.
    /// </summary>
    /// <param name="sqid">Sqid-encoded submission id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 stream / 404 / 409 ProblemDetails.</returns>
    [HttpGet("submissions/{sqid}/download")]
    public async Task<IActionResult> DownloadAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var subject = RequireSubject();
        if (subject is null) { return Unauthorized(); }

        // Ownership check first — surface a 404 for cross-subject lookups
        // so the existence of another consumer's batch never leaks.
        var existing = await _service.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (existing.IsFailure)
        {
            return MapFailureRaw(existing.ErrorCode!, existing.ErrorMessage!);
        }
        if (!string.Equals(existing.Value.ConsumerSubject, subject, StringComparison.Ordinal))
        {
            return NotFound(new { error = ErrorCodes.NotFound, message = "Submission not found." });
        }

        var bytesResult = await _service.GetDownloadBytesAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (bytesResult.IsFailure)
        {
            return MapFailureRaw(bytesResult.ErrorCode!, bytesResult.ErrorMessage!);
        }
        var info = bytesResult.Value.Info;

        Response.Headers["X-Batch-Hash-Sha256"] = info.HashSha256;
        Response.Headers["X-Batch-Signature-Hmac"] = info.SignatureBase64;
        Response.Headers["Content-Disposition"] =
            $"attachment; filename=\"{info.FileName}\"";
        return File(bytesResult.Value.Bytes, "text/csv; charset=utf-8");
    }

    /// <summary>
    /// <c>GET /api/interop/batch/submissions/{sqid}/rows</c> — paged
    /// rows-list for the supplied submission.
    /// </summary>
    /// <param name="sqid">Sqid-encoded submission id.</param>
    /// <param name="status">Optional row-status filter.</param>
    /// <param name="skip">Page offset (default 0).</param>
    /// <param name="take">Page size (default 100; max 200).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 page envelope.</returns>
    [HttpGet("submissions/{sqid}/rows")]
    public async Task<ActionResult<OfflineBatchRowPageDto>> ListRowsAsync(
        string sqid,
        [FromQuery] string? status = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        var subject = RequireSubject();
        if (subject is null) { return Unauthorized(); }
        var existing = await _service.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (existing.IsFailure)
        {
            return MapFailure<OfflineBatchRowPageDto>(existing.ErrorCode!, existing.ErrorMessage!);
        }
        if (!string.Equals(existing.Value.ConsumerSubject, subject, StringComparison.Ordinal))
        {
            return NotFound(new { error = ErrorCodes.NotFound, message = "Submission not found." });
        }
        var filter = new OfflineBatchRowFilterDto(Status: status, Skip: skip, Take: take);
        var result = await _service.ListRowsAsync(sqid, filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<OfflineBatchRowPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// <c>GET /api/interop/batch/submissions</c> — lists the caller's
    /// submissions. The consumer subject filter is pinned to the
    /// authenticated subject.
    /// </summary>
    /// <param name="opCode">Optional op-code filter.</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="skip">Page offset.</param>
    /// <param name="take">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 page envelope.</returns>
    [HttpGet("submissions")]
    public async Task<ActionResult<OfflineBatchSubmissionPageDto>> ListAsync(
        [FromQuery] string? opCode = null,
        [FromQuery] string? status = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var subject = RequireSubject();
        if (subject is null) { return Unauthorized(); }
        var filter = new OfflineBatchSubmissionFilterDto(
            ConsumerSubject: subject,
            OpCode: opCode,
            Status: status,
            Skip: skip,
            Take: take);
        var result = await _service.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<OfflineBatchSubmissionPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Returns the authenticated subject extracted from the JWT, or <c>null</c> when unauthenticated.</summary>
    /// <returns>The opaque subject string, or <c>null</c>.</returns>
    private string? RequireSubject()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? User.FindFirstValue("sub");
        return string.IsNullOrWhiteSpace(sub) ? null : sub;
    }

    /// <summary>Maps a failed <see cref="Result"/> to an <see cref="ActionResult{T}"/>.</summary>
    /// <typeparam name="T">Success DTO type.</typeparam>
    /// <param name="errorCode">Stable error code.</param>
    /// <param name="errorMessage">Human-readable message.</param>
    /// <returns>Mapped result.</returns>
    private ActionResult<T> MapFailure<T>(string errorCode, string errorMessage)
        => errorCode switch
        {
            ErrorCodes.InvalidSqid => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.ValidationFailed => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };

    /// <summary>Variant of <c>MapFailure</c> returning <see cref="IActionResult"/> for the streaming endpoint.</summary>
    /// <param name="errorCode">Stable error code.</param>
    /// <param name="errorMessage">Human-readable message.</param>
    /// <returns>Mapped action result.</returns>
    private IActionResult MapFailureRaw(string errorCode, string errorMessage)
        => errorCode switch
        {
            ErrorCodes.InvalidSqid => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.ValidationFailed => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
