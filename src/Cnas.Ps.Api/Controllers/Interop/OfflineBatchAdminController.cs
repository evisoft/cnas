using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Interop.Batch;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers.Interop;

/// <summary>
/// R1710 / TOR INT 002 — admin surface over the offline-batch registry.
/// Restricted to the <c>cnas-admin</c> policy so operators can inspect
/// submissions across all consumers (the consumer-facing controller pins
/// the lookup to the caller's subject).
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/interop/batch")]
public sealed class OfflineBatchAdminController : ControllerBase
{
    private readonly IOfflineBatchSubmissionService _service;

    /// <summary>Constructs the controller.</summary>
    /// <param name="service">Submission service.</param>
    public OfflineBatchAdminController(IOfflineBatchSubmissionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <summary>
    /// <c>GET /api/admin/interop/batch/submissions</c> — admin lookup
    /// across all consumers.
    /// </summary>
    /// <param name="consumerSubject">Optional consumer subject filter.</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="opCode">Optional op-code filter.</param>
    /// <param name="skip">Page offset (default 0).</param>
    /// <param name="take">Page size (default 50; max 100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 page envelope.</returns>
    [HttpGet("submissions")]
    public async Task<ActionResult<OfflineBatchSubmissionPageDto>> ListAsync(
        [FromQuery] string? consumerSubject = null,
        [FromQuery] string? status = null,
        [FromQuery] string? opCode = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new OfflineBatchSubmissionFilterDto(
            ConsumerSubject: consumerSubject,
            OpCode: opCode,
            Status: status,
            Skip: skip,
            Take: take);
        var result = await _service.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<OfflineBatchSubmissionPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Maps a failed result to an action result.</summary>
    /// <typeparam name="T">Success DTO type.</typeparam>
    /// <param name="errorCode">Stable error code.</param>
    /// <param name="errorMessage">Description.</param>
    /// <returns>Mapped action result.</returns>
    private ActionResult<T> MapFailure<T>(string errorCode, string errorMessage)
        => errorCode switch
        {
            ErrorCodes.ValidationFailed => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
