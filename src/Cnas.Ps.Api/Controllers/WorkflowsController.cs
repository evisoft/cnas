using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// UC16 — "Configurez flux de lucru" (configure workflow). REST surface over
/// <see cref="IWorkflowConfigurationService"/>. Functional administrators
/// (<see cref="AuthorizationComposition.CnasAdmin"/> policy) retrieve and persist workflow
/// definitions — JSON documents describing the state graph that drives an application's
/// lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Workflow code semantics.</b> Unlike most external identifiers in CNAS, the
/// <c>workflowCode</c> route segment is NOT a Sqid (CLAUDE.md RULE 3 does NOT apply).
/// Workflow codes are stable business identifiers chosen by administrators
/// (e.g. <c>WF-PENSION-AGE</c>, <c>WF-INDEMNIZATION</c>) — they ARE the public name of
/// the workflow definition rather than an opaque surrogate. Sqid-encoding would obscure
/// the very identifier the admin uses to refer to the workflow, so we keep the route
/// strings transparent.
/// </para>
/// <para>
/// Route table:
/// <list type="bullet">
///   <item><c>GET /api/workflows/{workflowCode}</c> — fetch the active definition (200 / 404).</item>
///   <item><c>PUT /api/workflows/{workflowCode}</c> — persist a new version (204 / 400).</item>
/// </list>
/// </para>
/// <para>
/// The body of <c>PUT</c> is the raw JSON workflow definition (Content-Type
/// <c>application/json</c>); we forward it verbatim to the service. Empty / whitespace
/// bodies are rejected by the service as <see cref="ErrorCodes.ValidationFailed"/>.
/// </para>
/// </remarks>
/// <param name="workflows">Underlying workflow-configuration service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/workflows")]
public sealed class WorkflowsController(IWorkflowConfigurationService workflows) : ControllerBase
{
    private readonly IWorkflowConfigurationService _workflows = workflows;

    /// <summary>
    /// R0121 / CF 16.02 — returns every workflow whose current version is active,
    /// ordered alphabetically by code. Backs the admin visual designer's list page.
    /// </summary>
    /// <param name="codeFilter">Optional free-text filter; case-insensitive contains-match against <c>Code</c>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the list (possibly empty); 400 ProblemDetails on service-layer validation failure.</returns>
    [HttpGet("")]
    public async Task<ActionResult<IReadOnlyList<WorkflowDefinitionListItem>>> ListAsync(
        [FromQuery] string? codeFilter = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _workflows.ListCurrentAsync(codeFilter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(result.ErrorMessage, statusCode: StatusForCode(result.ErrorCode));
    }

    /// <summary>
    /// Returns the active workflow definition JSON for <paramref name="workflowCode"/>. The
    /// raw JSON document is returned with content-type <c>application/json</c> via the
    /// <see cref="ContentResult"/> wrapper so clients can byte-for-byte round-trip it on
    /// a subsequent <c>PUT</c>.
    /// </summary>
    /// <param name="workflowCode">
    /// Workflow code identifier (NOT a Sqid — see remarks on the controller class).
    /// Example: <c>WF-PENSION-AGE</c>.
    /// </param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the JSON definition; 404 when no definition exists for this code.</returns>
    [HttpGet("{workflowCode}")]
    public async Task<IActionResult> GetDefinitionAsync(
        [FromRoute] string workflowCode,
        CancellationToken cancellationToken = default)
    {
        var result = await _workflows.GetDefinitionAsync(workflowCode, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return MapFailureBare(result.ErrorCode, result.ErrorMessage);
        }

        // Return the raw JSON document with the correct content-type so a client that
        // calls GET followed by PUT (e.g. an editor round-tripping the definition) sees
        // identical bytes on both sides.
        return Content(result.Value, "application/json");
    }

    /// <summary>
    /// Persists a new version of the workflow definition for <paramref name="workflowCode"/>.
    /// Previous versions remain queryable per the interface contract — this endpoint never
    /// destroys history; it appends a new revision.
    /// </summary>
    /// <param name="workflowCode">
    /// Workflow code identifier (NOT a Sqid — see remarks on the controller class).
    /// Example: <c>WF-PENSION-AGE</c>.
    /// </param>
    /// <param name="definitionJson">
    /// Raw JSON workflow definition. Read from the request body via the <see cref="FromBodyAttribute"/>.
    /// Empty / whitespace bodies are rejected by the service.
    /// </param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 No Content on success; 400 / 404 ProblemDetails on failure.</returns>
    [HttpPut("{workflowCode}")]
    [Consumes("application/json")]
    public async Task<IActionResult> SaveDefinitionAsync(
        [FromRoute] string workflowCode,
        [FromBody] System.Text.Json.JsonElement definitionJson,
        CancellationToken cancellationToken = default)
    {
        // Re-serialise the JsonElement to its canonical form so the service receives a
        // syntactically valid JSON string (we accept the element to leverage MVC's built-in
        // JSON parser for malformed-body rejection at the binder stage — bad JSON yields
        // 400 before the action runs).
        var json = definitionJson.GetRawText();
        var result = await _workflows.SaveDefinitionAsync(workflowCode, json, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0129 / CF 15.04 — returns the full history chain of workflow definitions for
    /// <paramref name="workflowCode"/>, ordered newest version first. The body is a JSON
    /// array of <see cref="WorkflowDefinitionHistoryItem"/> rows (no payloads — fetch the
    /// active version's body via the standard GET).
    /// </summary>
    /// <param name="workflowCode">Workflow code identifier (NOT a Sqid — see remarks on the class).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the history list (possibly empty); 400 on validation failure.</returns>
    [HttpGet("{workflowCode}/history")]
    public async Task<ActionResult<IReadOnlyList<WorkflowDefinitionHistoryItem>>> GetHistoryAsync(
        [FromRoute] string workflowCode,
        CancellationToken cancellationToken = default)
    {
        var result = await _workflows.GetHistoryAsync(workflowCode, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            var status = StatusForCode(result.ErrorCode);
            return status == StatusCodes.Status404NotFound
                ? NotFound()
                : Problem(result.ErrorMessage, statusCode: status);
        }
        return Ok(result.Value);
    }

    /// <summary>Maps a non-generic <see cref="Result"/> failure to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 400 ProblemDetails as appropriate.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>404 NotFound, 403 Forbidden, or 400 BadRequest.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
