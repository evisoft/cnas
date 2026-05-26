using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.LaborBooklet;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0922 / TOR Annex 2 §8.2.4 — REST surface for the InsuredPerson-attached
/// pre-1999 stagiu Years/Months/Days roll-up sub-table.
/// </summary>
/// <remarks>
/// Three endpoints: list, append, remove. All identifiers crossing the wire
/// are Sqid-encoded per CLAUDE.md RULE 3. The controller is gated by the
/// <c>cnas-user,cnas-admin</c> role pair — matches the parent registry
/// (Insured Persons) access policy.
/// </remarks>
[ApiController]
[Authorize(Roles = "cnas-user,cnas-admin")]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/insured-persons")]
public sealed class Pre1999StagiuController : ControllerBase
{
    private readonly IPre1999StagiuService _svc;
    private readonly IValidator<Pre1999StagiuInputDto> _validator;

    /// <summary>Constructs the controller with the underlying service façade.</summary>
    /// <param name="svc">Per-request stagiu service.</param>
    /// <param name="validator">FluentValidation validator for the input DTO.</param>
    public Pre1999StagiuController(
        IPre1999StagiuService svc,
        IValidator<Pre1999StagiuInputDto> validator)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(validator);
        _svc = svc;
        _validator = validator;
    }

    /// <summary>R0922 — list every active pre-1999 stagiu row for an InsuredPerson.</summary>
    /// <param name="sqid">Sqid-encoded InsuredPerson id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the list, 404 when the InsuredPerson is unknown.</returns>
    [HttpGet("{sqid}/pre1999-stagiu")]
    public async Task<ActionResult<IReadOnlyList<Pre1999StagiuDto>>> ListAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ListAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<IReadOnlyList<Pre1999StagiuDto>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0922 — append a fresh stagiu row to an InsuredPerson.</summary>
    /// <param name="sqid">Sqid-encoded InsuredPerson id.</param>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>201 with the persisted DTO on success; 400/404 on failure.</returns>
    [HttpPost("{sqid}/pre1999-stagiu")]
    public async Task<ActionResult<Pre1999StagiuDto>> AppendAsync(
        string sqid,
        [FromBody] Pre1999StagiuInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // FluentValidation gate — invariants (pre-1999 cutoff, normalised Y/M/D bounds)
        // must be enforced at the controller boundary so the service never sees malformed
        // input. Failures map to 400 + a comma-joined human-readable message.
        var validation = await _validator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _svc.AppendAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : MapFailure<Pre1999StagiuDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0922 — soft-delete the supplied stagiu row.</summary>
    /// <param name="sqid">Sqid-encoded InsuredPerson id (route prefix; not used for the lookup).</param>
    /// <param name="recordSqid">Sqid-encoded id of the stagiu row to remove.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>204 No Content on success; 400/404 on failure.</returns>
    [HttpDelete("{sqid}/pre1999-stagiu/{recordSqid}")]
    public async Task<IActionResult> RemoveAsync(
        string sqid,
        string recordSqid,
        CancellationToken cancellationToken = default)
    {
        _ = sqid;
        var result = await _svc.RemoveAsync(recordSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? NoContent()
            : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a service-layer failure to a typed <see cref="ActionResult{TValue}"/>.</summary>
    /// <typeparam name="T">DTO type the action would have returned on success.</typeparam>
    /// <param name="code">Stable error code from the service.</param>
    /// <param name="message">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ActionResult<T> MapFailure<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Maps a bare-result failure to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code from the service.</param>
    /// <param name="message">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>404 / 409 / 403 / 400 as appropriate.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Conflict => StatusCodes.Status409Conflict,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status400BadRequest,
    };
}
