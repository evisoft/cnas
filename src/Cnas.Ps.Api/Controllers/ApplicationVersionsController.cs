using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0321 / R0224 / UI 008 — REST surface for application autosave / draft-version
/// history. Drives <see cref="IApplicationVersionService"/>: every successful save
/// produces a new immutable <see cref="ApplicationVersion"/> snapshot under the
/// owning <see cref="ServiceApplication"/>; reverts and listings consume the same
/// history.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST /api/applications/{applicationSqid}/versions</c> — save a new version.</item>
///   <item><c>GET  /api/applications/{applicationSqid}/versions</c> — list version summaries.</item>
///   <item><c>GET  /api/applications/{applicationSqid}/versions/{versionNumber:int}</c> — fetch single version.</item>
///   <item><c>POST /api/applications/{applicationSqid}/versions/{versionNumber:int}/revert</c> — revert to that version.</item>
/// </list>
/// </para>
/// <para>
/// <b>Authorization.</b> The class is gated by the standard
/// <see cref="AuthorizeAttribute"/> (any authenticated caller). Per-request ownership
/// is enforced by the underlying service: the caller must either own the application
/// or hold one of the management roles (<c>cnas-decider</c>, <c>cnas-admin</c>,
/// <c>cnas-tech-admin</c>). Foreign callers surface as HTTP 403 via
/// <see cref="ErrorCodes.Forbidden"/>.
/// </para>
/// <para>
/// <b>Sqid convention.</b> Application ids are Sqid-encoded route segments per
/// CLAUDE.md RULE 3; malformed values surface as <see cref="ErrorCodes.InvalidSqid"/>
/// → HTTP 400. Version numbers are plain integers (1-based monotonic per application)
/// and live on the route as <c>{versionNumber:int}</c> so the framework rejects
/// non-numeric values for us.
/// </para>
/// <para>
/// <b>Rate limiting.</b> The class opts into the standard authenticated-user rate
/// limiter. A per-applicant policy specifically for autosave traffic is deferred —
/// see the batch decision notes.
/// </para>
/// </remarks>
/// <param name="versions">Underlying versioning service.</param>
/// <param name="validator">FluentValidation rule set for <see cref="ApplicationVersionSaveDto"/>.</param>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/applications/{applicationSqid}/versions")]
public sealed class ApplicationVersionsController(
    IApplicationVersionService versions,
    IValidator<ApplicationVersionSaveDto> validator) : ControllerBase
{
    private readonly IApplicationVersionService _versions = versions;
    private readonly IValidator<ApplicationVersionSaveDto> _validator = validator;

    /// <summary>
    /// Persists a new version snapshot of the application identified by
    /// <paramref name="applicationSqid"/>. The caller must be the applicant or a
    /// management-role holder.
    /// </summary>
    /// <param name="applicationSqid">Sqid-encoded id of the application.</param>
    /// <param name="input">Save payload (form data, source, optional note).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 201 Created on success with the saved row in the body; 400 on validation or
    /// Sqid failure; 403 when the caller is not the owner / manager; 404 when the
    /// application does not exist; 409 when the application is in a terminal status.
    /// </returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<IActionResult> SaveAsync(
        [FromRoute] string applicationSqid,
        [FromBody] ApplicationVersionSaveDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _validator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                detail: string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Already validated above — Enum.Parse is safe because the validator's BeKnownSource
        // rule rejects anything that does not parse case-sensitively.
        var source = Enum.Parse<ApplicationVersionSource>(input.Source, ignoreCase: false);

        var result = await _versions.SaveAsync(
            applicationSqid,
            input.FormDataJson,
            source,
            input.Note,
            cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return MapFailure(result.ErrorCode, result.ErrorMessage);
        }

        // 201 Created with a Location header pointing at the single-version GET.
        return CreatedAtAction(
            actionName: nameof(GetAsync),
            routeValues: new { applicationSqid, versionNumber = result.Value.VersionNumber },
            value: result.Value);
    }

    /// <summary>
    /// Lists every version row for the application identified by
    /// <paramref name="applicationSqid"/>, most recent first. The <c>FormDataJson</c>
    /// payload is omitted from each row to keep the response small.
    /// </summary>
    /// <param name="applicationSqid">Sqid-encoded id of the application.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the list; 400 / 403 / 404 on failure.</returns>
    [HttpGet]
    public async Task<IActionResult> ListAsync(
        [FromRoute] string applicationSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _versions.ListAsync(applicationSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Fetches a single version (with the <c>FormDataJson</c> payload) by version
    /// number under the application identified by <paramref name="applicationSqid"/>.
    /// </summary>
    /// <param name="applicationSqid">Sqid-encoded id of the application.</param>
    /// <param name="versionNumber">Numeric version number under that application.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the row; 400 / 403 / 404 on failure.</returns>
    [HttpGet("{versionNumber:int}")]
    public async Task<IActionResult> GetAsync(
        [FromRoute] string applicationSqid,
        [FromRoute] int versionNumber,
        CancellationToken cancellationToken = default)
    {
        var result = await _versions.GetAsync(applicationSqid, versionNumber, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Restores the application to the supplied <paramref name="versionNumber"/> by
    /// saving its <c>FormDataJson</c> as a fresh row with source
    /// <see cref="ApplicationVersionSource.Revert"/>. The response body is the
    /// newly-written row (NOT the target).
    /// </summary>
    /// <param name="applicationSqid">Sqid-encoded id of the application.</param>
    /// <param name="versionNumber">Numeric version number to revert to.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the new revert row; 400 / 403 / 404 / 409 on failure.</returns>
    [HttpPost("{versionNumber:int}/revert")]
    public async Task<IActionResult> RevertAsync(
        [FromRoute] string applicationSqid,
        [FromRoute] int versionNumber,
        CancellationToken cancellationToken = default)
    {
        var result = await _versions.RevertAsync(applicationSqid, versionNumber, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
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
    /// Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code. The
    /// <see cref="ErrorCodes.ApplicationNotEditable"/> code maps to 409 Conflict because
    /// the request is well-formed but conflicts with the application's terminal state.
    /// </summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>HTTP status code.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.ApplicationNotEditable => StatusCodes.Status409Conflict,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
