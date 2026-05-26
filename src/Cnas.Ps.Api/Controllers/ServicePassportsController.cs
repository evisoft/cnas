using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// UC15 — "Configurez serviciu electronic" (configure electronic service). REST surface
/// over <see cref="IServicePassportService"/>. Functional administrators
/// (<see cref="AuthorizationComposition.CnasAdmin"/> policy) create, update, and inspect
/// <see cref="ServicePassportInput"/> / <see cref="ServicePassportDetailOutput"/> records
/// — the JSON-schema-driven definitions that link a public service to its workflow code,
/// form schema, decision rules, and metadata.
/// </summary>
/// <remarks>
/// <para>
/// Per CLAUDE.md RULE 3 every external identifier on this surface is a Sqid-encoded
/// string. The service layer decodes/encodes at its boundary so the controller is a thin
/// pass-through: it forwards the Sqid string verbatim to the service and propagates the
/// service's <see cref="Result{T}"/> outcome through the standard
/// <c>StatusForCode</c> translation pattern shared with
/// <see cref="AdminController"/> and <see cref="UsersController"/>.
/// </para>
/// <para>
/// Route table:
/// <list type="bullet">
///   <item><c>GET    /api/service-passports</c>          — list active passports (200).</item>
///   <item><c>GET    /api/service-passports/{sqid}</c>   — read one by Sqid (200 / 404 / 400).</item>
///   <item><c>POST   /api/service-passports</c>         — create (input.Id null → 201).</item>
///   <item><c>PUT    /api/service-passports/{sqid}</c>  — update; route id wins over body (200 / 404 / 400).</item>
/// </list>
/// </para>
/// </remarks>
/// <param name="passports">Underlying service-passport admin service.</param>
/// <param name="configMatrix">R0143 / CF 17.19 — per-passport configuration matrix service.</param>
/// <param name="rulesEditor">R0141 / CF 15.03 — per-passport business-rule editor service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/service-passports")]
public sealed class ServicePassportsController(
    IServicePassportService passports,
    IServicePassportConfigMatrixService configMatrix,
    IServicePassportRulesEditorService rulesEditor) : ControllerBase
{
    private readonly IServicePassportService _passports = passports;
    private readonly IServicePassportConfigMatrixService _configMatrix = configMatrix;
    private readonly IServicePassportRulesEditorService _rulesEditor = rulesEditor;

    /// <summary>
    /// Lists every active service passport with a compact <see cref="ServicePassportListItem"/>
    /// row (Sqid id + code + name + enabled flag). Used by the admin UI to populate the
    /// passport catalogue browser.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with the catalogue (Sqid-encoded ids per CLAUDE.md RULE 3); 400 ProblemDetails
    /// on unexpected service failure.
    /// </returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ServicePassportListItem>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _passports.ListAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<IReadOnlyList<ServicePassportListItem>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Returns the full <see cref="ServicePassportDetailOutput"/> for the passport whose
    /// external Sqid is <paramref name="sqid"/>. The service decodes the Sqid; a malformed
    /// id surfaces as 400 (<see cref="ErrorCodes.InvalidSqid"/>) and a soft-deleted /
    /// unknown id surfaces as 404.
    /// </summary>
    /// <param name="sqid">Sqid-encoded passport id (CLAUDE.md RULE 3).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with detail; 404 when not found; 400 on malformed Sqid.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<ServicePassportDetailOutput>> GetAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _passports.GetAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<ServicePassportDetailOutput>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Creates a new service passport. The service treats an absent / null <c>Input.Id</c>
    /// as the create signal — see <see cref="IServicePassportService.UpsertAsync"/>. On
    /// success the response is a 201 Created pointing at <see cref="GetAsync"/> with the
    /// newly-assigned Sqid in the body and the <c>Location</c> header.
    /// </summary>
    /// <param name="input">
    /// Passport definition body. The <c>Id</c> field is ignored on this endpoint (the
    /// controller deliberately overrides it with <c>null</c>) so callers cannot smuggle a
    /// pre-existing id through the create surface — CLAUDE.md §2.4 (mass-assignment
    /// prevention).
    /// </param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>201 with the new Sqid; 400 on validation failure.</returns>
    [HttpPost]
    public async Task<ActionResult<string>> CreateAsync(
        [FromBody] ServicePassportInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Defense in depth — force the Id to null so the service follows its create branch
        // even if the caller submitted a populated Id field. CLAUDE.md §2.4.
        var createInput = input with { Id = null };
        var result = await _passports.UpsertAsync(createInput, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetAsync), new { sqid = result.Value }, result.Value)
            : MapFailureGeneric<string>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Updates an existing service passport. The route Sqid is authoritative: the body's
    /// own <c>Id</c> field is overridden with the route value so callers cannot rename a
    /// different passport by pointing one route id at a body carrying another. The service
    /// decodes the Sqid; an unknown / malformed id surfaces as 404 / 400 respectively.
    /// </summary>
    /// <param name="sqid">Sqid-encoded passport id (CLAUDE.md RULE 3).</param>
    /// <param name="input">Updated passport definition; the body's <c>Id</c> is overridden.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the (unchanged) Sqid; 404 when missing; 400 on validation failure.</returns>
    [HttpPut("{sqid}")]
    public async Task<ActionResult<string>> UpdateAsync(
        [FromRoute] string sqid,
        [FromBody] ServicePassportInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Route id wins — see action XML doc. Re-binding the input with the route Sqid is
        // cheaper and safer than asserting that the body and route agree, because record
        // copy semantics make the override trivially unambiguous.
        var updateInput = input with { Id = sqid };
        var result = await _passports.UpsertAsync(updateInput, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<string>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0142 / CF 15.04 — returns the full version chain for the passport addressed by
    /// <paramref name="sqid"/>. The body is a JSON array of
    /// <see cref="ServicePassportHistoryItem"/> rows ordered Version DESC.
    /// </summary>
    /// <param name="sqid">Sqid id of any revision row (current or historical).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the history list; 404 when the row does not exist; 400 on malformed Sqid.</returns>
    [HttpGet("{sqid}/history")]
    public async Task<ActionResult<IReadOnlyList<ServicePassportHistoryItem>>> GetHistoryAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _passports.GetHistoryAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<IReadOnlyList<ServicePassportHistoryItem>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0143 / CF 17.19 — returns the eight-column configuration matrix for the
    /// passport identified by <paramref name="code"/>. The code is the stable logical
    /// passport code (e.g. <c>SP-3.1-A-BIRTH-GRANT</c>), NOT a Sqid — it is the public
    /// identifier shared with external systems just like
    /// <see cref="Cnas.Ps.Core.Domain.ServicePassport.Code"/>.
    /// </summary>
    /// <param name="code">Logical passport code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the matrix; 404 when no active current row matches the code.</returns>
    [HttpGet("{code}/config-matrix")]
    public async Task<ActionResult<ServicePassportConfigMatrixDto>> GetConfigMatrixAsync(
        [FromRoute] string code,
        CancellationToken cancellationToken = default)
    {
        var result = await _configMatrix.GetMatrixAsync(code, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<ServicePassportConfigMatrixDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0141 / TOR CF 15.03 — lists the business rules attached to the current
    /// revision of the passport identified by <paramref name="code"/>.
    /// </summary>
    /// <param name="code">Stable logical passport code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the rule list (empty when no rules configured); 404 when the code is unknown.</returns>
    [HttpGet("{code}/business-rules")]
    public async Task<ActionResult<IReadOnlyList<BusinessRuleDto>>> ListBusinessRulesAsync(
        [FromRoute] string code,
        CancellationToken cancellationToken = default)
    {
        var result = await _rulesEditor.ListRulesAsync(code, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<IReadOnlyList<BusinessRuleDto>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0141 / TOR CF 15.03 — creates or replaces a business rule on the
    /// current revision of the passport identified by <paramref name="code"/>.
    /// The input's <c>Id</c> field discriminates create (null) vs update
    /// (existing opaque id).
    /// </summary>
    /// <param name="code">Stable logical passport code.</param>
    /// <param name="input">Desired business-rule state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the persisted rule; 400 on validation failure; 404 when the passport or referenced rule is unknown.</returns>
    [HttpPost("{code}/business-rules")]
    public async Task<ActionResult<BusinessRuleDto>> UpsertBusinessRuleAsync(
        [FromRoute] string code,
        [FromBody] BusinessRuleInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _rulesEditor.UpsertRuleAsync(code, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<BusinessRuleDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0141 / TOR CF 15.03 — deletes the business rule identified by
    /// <paramref name="ruleSqid"/> from the current revision of the passport.
    /// </summary>
    /// <param name="code">Stable logical passport code.</param>
    /// <param name="ruleSqid">Opaque stable rule id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 on deletion; 404 when the passport or rule is unknown.</returns>
    [HttpDelete("{code}/business-rules/{ruleSqid}")]
    public async Task<IActionResult> DeleteBusinessRuleAsync(
        [FromRoute] string code,
        [FromRoute] string ruleSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _rulesEditor.DeleteRuleAsync(code, ruleSqid, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return NoContent();
        }
        var status = StatusForCode(result.ErrorCode);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(result.ErrorMessage, statusCode: status);
    }

    /// <summary>Maps a <see cref="Result{T}"/> failure to an <see cref="ActionResult{T}"/>.</summary>
    /// <typeparam name="T">The DTO type the action would have returned on success.</typeparam>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 400 ProblemDetails as appropriate.</returns>
    private ActionResult<T> MapFailureGeneric<T>(string? code, string? message)
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
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
