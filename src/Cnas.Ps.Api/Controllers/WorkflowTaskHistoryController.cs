using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Workflow;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0125 / CF 16.09 — admin REST surface over the workflow-task history projection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>GET /api/workflow-tasks/{sqid}/history</c> — paged history for one task.</item>
/// </list>
/// </para>
/// <para>
/// <b>Auth.</b> Authenticated cnas-user or owner; the underlying projection rows carry
/// step codes + actor sqids and are intended for admin and dossier-owner consumption.
/// The controller-level <see cref="AuthorizationComposition.CnasUser"/> policy is
/// the floor; finer-grained ownership checks live in the service layer.
/// </para>
/// </remarks>
/// <param name="history">Underlying history service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasUser)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/workflow-tasks/{sqid}/history")]
public sealed class WorkflowTaskHistoryController(IWorkflowTaskHistoryService history) : ControllerBase
{
    private readonly IWorkflowTaskHistoryService _history = history;

    /// <summary>
    /// Returns the paged history projection for the supplied workflow task. The page is
    /// chronologically ordered ascending. Optional <paramref name="eventKind"/> filter
    /// restricts the result to one transition kind (e.g. <c>"Reassigned"</c>).
    /// </summary>
    /// <param name="sqid">Sqid-encoded workflow task id.</param>
    /// <param name="eventKind">Optional event-kind name filter.</param>
    /// <param name="skip">Zero-based row offset.</param>
    /// <param name="take">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the page on success; 400 on bad sqid / filter.</returns>
    [HttpGet]
    public async Task<IActionResult> GetAsync(
        [FromRoute] string sqid,
        [FromQuery] string? eventKind = null,
        [FromQuery] int? skip = null,
        [FromQuery] int? take = null,
        CancellationToken cancellationToken = default)
    {
        var filter = new WorkflowTaskHistoryFilterDto(eventKind, skip, take);
        var result = await _history.GetHistoryAsync(sqid, filter, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }
        return MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a service-layer failure to an HTTP response.</summary>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Detail message.</param>
    /// <returns>404 / 400 ProblemDetails as appropriate.</returns>
    private IActionResult MapFailure(string? code, string? message) => code switch
    {
        ErrorCodes.NotFound => NotFound(),
        ErrorCodes.Unauthorized => Unauthorized(),
        ErrorCodes.Forbidden => Forbid(),
        _ => Problem(message, statusCode: StatusCodes.Status400BadRequest),
    };
}
