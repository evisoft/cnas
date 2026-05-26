using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Treasury.Feed;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R1810 / TOR BP 1.2-I — admin REST surface over the daily Treasury feed
/// import registry. Restricted to the
/// <see cref="AuthorizationComposition.CnasAdmin"/> policy because manual
/// imports touch the BASS receipts aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST /api/admin/treasury-feed/imports?feedDate=YYYY-MM-DD</c> — trigger manual import.</item>
///   <item><c>GET  /api/admin/treasury-feed/imports/{sqid}</c> — get import summary.</item>
///   <item><c>GET  /api/admin/treasury-feed/imports/{sqid}/details</c> — get import + paged rows.</item>
///   <item><c>GET  /api/admin/treasury-feed/imports</c> — list imports with filters.</item>
/// </list>
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/treasury-feed")]
public sealed class TreasuryFeedAdminController : ControllerBase
{
    private readonly ITreasuryFeedAdminService _service;

    /// <summary>Constructs the controller.</summary>
    /// <param name="service">Treasury feed admin façade.</param>
    public TreasuryFeedAdminController(ITreasuryFeedAdminService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <summary>
    /// Triggers a manual import for the supplied feed date.
    /// </summary>
    /// <param name="feedDate">ISO yyyy-MM-dd feed date.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the import summary; 400 / 409 on failure.</returns>
    [HttpPost("imports")]
    public async Task<ActionResult<TreasuryFeedImportSummaryDto>> TriggerManualImportAsync(
        [FromQuery] string feedDate,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feedDate)
            || !DateOnly.TryParseExact(feedDate, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return BadRequest(new
            {
                error = ErrorCodes.ValidationFailed,
                message = "feedDate query parameter is required as ISO yyyy-MM-dd.",
            });
        }
        var result = await _service.TriggerManualImportAsync(parsed, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<TreasuryFeedImportSummaryDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Fetches a single import by its Sqid (no rows attached).
    /// </summary>
    /// <param name="sqid">Sqid-encoded import id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 / 400 / 404.</returns>
    [HttpGet("imports/{sqid}")]
    public async Task<ActionResult<TreasuryFeedImportDto>> GetImportAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetImportByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<TreasuryFeedImportDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Fetches a single import together with a filtered, paged subset of its
    /// rows.
    /// </summary>
    /// <param name="sqid">Sqid-encoded import id.</param>
    /// <param name="status">Optional status filter (stable enum-name string).</param>
    /// <param name="skip">Page offset (default 0).</param>
    /// <param name="take">Page size (default 100; max 200).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the details envelope; 400 / 404 on failure.</returns>
    [HttpGet("imports/{sqid}/details")]
    public async Task<ActionResult<TreasuryFeedImportDetailsDto>> GetImportDetailsAsync(
        string sqid,
        [FromQuery] string? status = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        var filter = new TreasuryFeedImportRowFilterDto(Status: status, Skip: skip, Take: take);
        var result = await _service.GetImportDetailsAsync(sqid, filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<TreasuryFeedImportDetailsDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Lists imports matching the supplied filter envelope.
    /// </summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="feedDateFrom">Optional inclusive lower-bound date.</param>
    /// <param name="feedDateTo">Optional inclusive upper-bound date.</param>
    /// <param name="triggerKind">Optional trigger-kind filter.</param>
    /// <param name="skip">Page offset (default 0).</param>
    /// <param name="take">Page size (default 50; max 100).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the page DTO; 400 on validation failure.</returns>
    [HttpGet("imports")]
    public async Task<ActionResult<TreasuryFeedImportPageDto>> ListAsync(
        [FromQuery] string? status = null,
        [FromQuery] DateOnly? feedDateFrom = null,
        [FromQuery] DateOnly? feedDateTo = null,
        [FromQuery] string? triggerKind = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new TreasuryFeedImportFilterDto(
            Status: status,
            FeedDateFrom: feedDateFrom,
            FeedDateTo: feedDateTo,
            TriggerKind: triggerKind,
            Skip: skip,
            Take: take);
        var result = await _service.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<TreasuryFeedImportPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Maps a failed <see cref="Result{T}"/> to the appropriate HTTP status:
    /// <c>INVALID_SQID</c> / <c>VALIDATION_FAILED</c> → 400,
    /// <c>NOT_FOUND</c> → 404, <c>CONFLICT</c> → 409, anything else → 500.
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
