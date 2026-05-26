using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.DataClassification;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2279 / TOR SEC 033 — admin REST surface over the data-classification
/// catalog registry. Restricted to the
/// <see cref="AuthorizationComposition.CnasAdmin"/> policy because the catalog
/// exposes the internal classification policy of every Contracts DTO.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST /api/admin/classification/snapshots</c> — capture a manual snapshot.</item>
///   <item><c>GET  /api/admin/classification/snapshots/{sqid}</c> — snapshot summary.</item>
///   <item><c>GET  /api/admin/classification/snapshots/{sqid}/details</c> — snapshot + paged entries.</item>
///   <item><c>GET  /api/admin/classification/snapshots?take=20</c> — list recent snapshots.</item>
///   <item><c>POST /api/admin/classification/drift?baselineSnapshotSqid=…&amp;currentSnapshotSqid=…</c> — compute drift (idempotent).</item>
///   <item><c>GET  /api/admin/classification/drift?...</c> — list drift findings.</item>
///   <item><c>POST /api/admin/classification/drift/{sqid}/acknowledge</c> — acknowledge a finding.</item>
/// </list>
/// </para>
/// </remarks>
/// <param name="service">Classification catalog service façade.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/classification")]
public sealed class ClassificationCatalogAdminController(IClassificationCatalogService service) : ControllerBase
{
    private readonly IClassificationCatalogService _service = service;

    /// <summary>Captures a fresh classification-catalog snapshot.</summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the captured <see cref="ClassificationCatalogSnapshotDto"/>.</returns>
    [HttpPost("snapshots")]
    public async Task<ActionResult<ClassificationCatalogSnapshotDto>> CaptureSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _service.CaptureManualSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ClassificationCatalogSnapshotDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Fetches a single snapshot summary by its Sqid.</summary>
    /// <param name="sqid">Sqid-encoded snapshot id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 / 400 / 404.</returns>
    [HttpGet("snapshots/{sqid}")]
    public async Task<ActionResult<ClassificationCatalogSnapshotDto>> GetSnapshotAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetSnapshotByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ClassificationCatalogSnapshotDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Fetches a snapshot with a paged + filtered slice of its entries.</summary>
    /// <param name="sqid">Sqid-encoded snapshot id.</param>
    /// <param name="label">Optional label-name filter.</param>
    /// <param name="isExplicit">Optional explicit-attribute filter.</param>
    /// <param name="typeFullNameContains">Optional case-sensitive substring filter.</param>
    /// <param name="skip">Page offset (≥ 0; default 0).</param>
    /// <param name="take">Page size (1..500; default 100).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 / 400 / 404.</returns>
    [HttpGet("snapshots/{sqid}/details")]
    public async Task<ActionResult<ClassificationCatalogSnapshotDetailsDto>> GetSnapshotDetailsAsync(
        string sqid,
        [FromQuery] string? label = null,
        [FromQuery] bool? isExplicit = null,
        [FromQuery] string? typeFullNameContains = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        var filter = new ClassificationCatalogEntryFilterDto(
            Label: label,
            IsExplicit: isExplicit,
            TypeFullNameContains: typeFullNameContains,
            Skip: skip,
            Take: take);
        var result = await _service.GetSnapshotDetailsAsync(sqid, filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ClassificationCatalogSnapshotDetailsDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists the most-recent snapshots.</summary>
    /// <param name="take">Page size (1..100; default 20).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the page, or 400 on validation failure.</returns>
    [HttpGet("snapshots")]
    public async Task<ActionResult<ClassificationCatalogSnapshotPageDto>> ListSnapshotsAsync(
        [FromQuery] int take = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ListSnapshotsAsync(take, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ClassificationCatalogSnapshotPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Computes drift between two snapshots (idempotent — safe to retry).</summary>
    /// <param name="baselineSnapshotSqid">Sqid of the baseline snapshot.</param>
    /// <param name="currentSnapshotSqid">Sqid of the current snapshot.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the drift envelope, or 400 / 404 on failure.</returns>
    [HttpPost("drift")]
    public async Task<ActionResult<ClassificationDriftResultDto>> ComputeDriftAsync(
        [FromQuery] string baselineSnapshotSqid,
        [FromQuery] string currentSnapshotSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ComputeDriftAsync(baselineSnapshotSqid, currentSnapshotSqid, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ClassificationDriftResultDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists drift findings filtered by kind / acknowledgement state.</summary>
    /// <param name="driftKind">Optional drift-kind filter.</param>
    /// <param name="acknowledged">Optional acknowledgement-state filter.</param>
    /// <param name="skip">Page offset (≥ 0; default 0).</param>
    /// <param name="take">Page size (1..200; default 50).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the page, or 400 on validation failure.</returns>
    [HttpGet("drift")]
    public async Task<ActionResult<ClassificationDriftPageDto>> ListDriftFindingsAsync(
        [FromQuery] string? driftKind = null,
        [FromQuery] bool? acknowledged = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new ClassificationDriftFilterDto(
            DriftKind: driftKind,
            Acknowledged: acknowledged,
            Skip: skip,
            Take: take);
        var result = await _service.ListDriftFindingsAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ClassificationDriftPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Acknowledges a drift finding with an operator-supplied note.</summary>
    /// <param name="sqid">Sqid-encoded finding id.</param>
    /// <param name="input">Acknowledgement payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated DTO, or 400 / 404 / 409.</returns>
    [HttpPost("drift/{sqid}/acknowledge")]
    [Consumes("application/json")]
    public async Task<ActionResult<ClassificationDriftFindingDto>> AcknowledgeDriftAsync(
        string sqid,
        [FromBody] ClassificationDriftAcknowledgeInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.AcknowledgeDriftAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ClassificationDriftFindingDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Translates a failed <see cref="Result{T}"/> into the appropriate
    /// <see cref="ActionResult"/>: <c>INVALID_SQID</c> / <c>VALIDATION_FAILED</c>
    /// → 400, <c>NOT_FOUND</c> → 404, <c>CONFLICT</c> → 409, anything else → 500.
    /// </summary>
    /// <typeparam name="T">DTO type that would have been returned on success.</typeparam>
    /// <param name="errorCode">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="errorMessage">Human-readable description.</param>
    /// <returns>An <see cref="ActionResult{T}"/> carrying the appropriate HTTP status.</returns>
    private ActionResult<T> MapFailure<T>(string errorCode, string errorMessage)
        => errorCode switch
        {
            ErrorCodes.InvalidSqid => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.ValidationFailed => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
