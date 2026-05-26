using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Interop.Batch;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers.Interop;

/// <summary>
/// R2161 / TOR INT 002 — REST surface for the generic CnasUser-facing
/// offline-batch ingest / export endpoints. Sits alongside the R1710 Annex-4
/// B2B controller (<see cref="OfflineBatchController"/>) — both are mounted
/// at the same time but cover different consumers (admin/B2B vs end-user
/// ad-hoc workflows). Every route is gated by the <c>CnasUser</c> policy
/// and the <c>Authenticated</c> rate-limit bucket.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasUser)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/offline-batch")]
public sealed class OfflineBatchJobsController : ControllerBase
{
    private readonly IOfflineBatchService _service;

    /// <summary>Constructs the controller.</summary>
    /// <param name="service">Generic offline-batch façade.</param>
    public OfflineBatchJobsController(IOfflineBatchService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <summary>
    /// <c>POST /api/offline-batch/ingest</c> — submits a multi-record ingest
    /// payload. The service rejects payloads of more than 10 000 rows with
    /// <c>OFFLINE_BATCH.PAYLOAD_TOO_LARGE</c>. On success the response is
    /// <c>201 Created</c> with a <see cref="OfflineBatchJobDto"/> in the body
    /// and a <c>Location</c> header pointing at the status route.
    /// </summary>
    /// <param name="body">Ingest input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>201 / 400 / 401.</returns>
    [HttpPost("ingest")]
    public async Task<ActionResult<OfflineBatchJobDto>> SubmitIngestAsync(
        [FromBody] OfflineBatchIngestInputDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await _service.SubmitIngestAsync(body, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? CreatedAtAction(
                nameof(GetStatusAsync),
                new { sqid = result.Value.Id },
                result.Value)
            : MapFailure<OfflineBatchJobDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// <c>POST /api/offline-batch/export</c> — submits a multi-record export
    /// request. Identical shape to the ingest endpoint above; row count is
    /// validated against the same 10 000 cap.
    /// </summary>
    /// <param name="body">Export input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>201 / 400 / 401.</returns>
    [HttpPost("export")]
    public async Task<ActionResult<OfflineBatchJobDto>> SubmitExportAsync(
        [FromBody] OfflineBatchExportInputDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await _service.SubmitExportAsync(body, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? CreatedAtAction(
                nameof(GetStatusAsync),
                new { sqid = result.Value.Id },
                result.Value)
            : MapFailure<OfflineBatchJobDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// <c>GET /api/offline-batch/{sqid}</c> — returns the current state of
    /// the supplied job. The lookup is scoped to the calling user — another
    /// user's job will surface as <c>404 NOT_FOUND</c> so the row's existence
    /// never leaks across users.
    /// </summary>
    /// <param name="sqid">Sqid-encoded job id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 / 400 / 401 / 404.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<OfflineBatchJobDto>> GetStatusAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetStatusAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<OfflineBatchJobDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Maps a failed <see cref="Result"/> to an <see cref="ActionResult{T}"/>.
    /// The mapping mirrors the convention used elsewhere in the offline-batch
    /// surface — payload-too-large is reported as <c>400 Bad Request</c>
    /// because it is a validation-style guard, not a server-side conflict.
    /// </summary>
    /// <typeparam name="T">Success DTO type.</typeparam>
    /// <param name="errorCode">Stable error code.</param>
    /// <param name="errorMessage">Human-readable message.</param>
    /// <returns>Mapped result.</returns>
    private ActionResult<T> MapFailure<T>(string errorCode, string errorMessage)
        => errorCode switch
        {
            ErrorCodes.InvalidSqid => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.ValidationFailed => BadRequest(new { error = errorCode, message = errorMessage }),
            IOfflineBatchService.PayloadTooLargeCode => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
