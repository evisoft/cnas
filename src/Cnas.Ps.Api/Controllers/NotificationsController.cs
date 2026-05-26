using Cnas.Ps.Api.Composition;
using Cnas.Ps.Contracts;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>UC22 — Notifications inbox REST surface.</summary>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/notifications")]
public sealed class NotificationsController(INotificationService notifications) : ControllerBase
{
    /// <summary>
    /// Read the caller's notification inbox. Supports R0371 filter parameters
    /// (<paramref name="unreadOnly"/> + <paramref name="channel"/>) so the dashboard
    /// history view can switch between "all", "unread", and per-channel slices.
    /// </summary>
    /// <param name="page">1-based page index (default 1).</param>
    /// <param name="pageSize">Items per page (clamped to [1, 200] server-side).</param>
    /// <param name="unreadOnly">When <c>true</c>, returns only rows with <c>readAtUtc</c> null.</param>
    /// <param name="channel">
    /// Optional channel filter (<c>InApp</c>, <c>Email</c>, <c>Sms</c>); case-insensitive.
    /// Unknown values yield 400 with <see cref="ErrorCodes.ValidationFailed"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 OK with a paged inbox; 400 on filter validation failures.</returns>
    [HttpGet("mine")]
    public async Task<ActionResult<PagedResult<NotificationOutput>>> MineAsync(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool unreadOnly = false,
        [FromQuery] string? channel = null,
        CancellationToken cancellationToken = default)
    {
        var query = new NotificationInboxQuery(new PageRequest(page, pageSize), unreadOnly, channel);
        var result = await notifications.InboxAsync(query, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(result.ErrorMessage, statusCode: MapStatusForCode(result.ErrorCode));
    }

    /// <summary>Mark a notification as read.</summary>
    /// <param name="id">Sqid-encoded notification id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 No Content on success; 404 when the row is missing.</returns>
    [HttpPost("{id}/read")]
    public async Task<IActionResult> MarkReadAsync(string id, CancellationToken cancellationToken = default)
    {
        var result = await notifications.MarkReadAsync(new MarkNotificationReadInput(id), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : NotFound();
    }

    /// <summary>
    /// R0371 — bulk-marks every unread inbox row for the caller as read. Returns the
    /// number of rows flipped so the UI can render a "marked N notifications" toast.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 OK with the count; 401 when the principal is anonymous.</returns>
    [HttpPost("mine/mark-all-read")]
    public async Task<ActionResult<MarkAllReadResponse>> MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        var result = await notifications.MarkAllReadAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new MarkAllReadResponse(result.Value))
            : Problem(result.ErrorMessage, statusCode: MapStatusForCode(result.ErrorCode));
    }

    /// <summary>
    /// Maps a <see cref="ErrorCodes"/> string onto an HTTP status code. Mirrors the
    /// patterns used by <see cref="TasksController"/> — keeps the inbox surface honest
    /// about 400 / 401 / 404 transitions.
    /// </summary>
    /// <param name="code">Error code string (may be null).</param>
    /// <returns>HTTP status code.</returns>
    private static int MapStatusForCode(string? code) => code switch
    {
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}

/// <summary>
/// R0371 — response body of <c>POST /api/notifications/mine/mark-all-read</c>.
/// </summary>
/// <param name="MarkedCount">Number of rows transitioned from unread to read by this call.</param>
public sealed record MarkAllReadResponse(int MarkedCount);
