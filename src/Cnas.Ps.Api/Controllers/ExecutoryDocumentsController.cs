using Cnas.Ps.Application.ExecutoryDocuments;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R1600 / R1406 / TOR Annex 3.8 / §3.6-G — REST surface for the executory-
/// documents (documente executorii) registry. Exposes register / modify /
/// suspend / resume / cancel / complete + list endpoints.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation.</b> Every endpoint is gated by the
/// <c>cnas-admin,cnas-user</c> roles — citizens never see this surface.
/// </para>
/// <para>
/// <b>Sqid round-trip.</b> Route parameters are decoded via
/// <see cref="ISqidService.TryDecode"/> before reaching the service layer;
/// outbound DTOs carry Sqid-encoded ids per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "cnas-admin,cnas-user")]
public sealed class ExecutoryDocumentsController : ControllerBase
{
    private readonly IExecutoryDocumentService _svc;

    /// <summary>Constructs the controller with its collaborators.</summary>
    /// <param name="svc">Executory-document service façade.</param>
    public ExecutoryDocumentsController(IExecutoryDocumentService svc)
    {
        ArgumentNullException.ThrowIfNull(svc);
        _svc = svc;
    }

    /// <summary>R1600 — registers a new executory document.</summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>201 with the persisted DTO on success; 400/409 on failure.</returns>
    [HttpPost("api/executory-documents")]
    public async Task<ActionResult<ExecutoryDocumentDto>> RegisterAsync(
        [FromBody] ExecutoryDocumentRegisterInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.RegisterAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : MapFailure<ExecutoryDocumentDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R1600 — modifies an outstanding executory document.</summary>
    /// <param name="sqid">Sqid-encoded document id.</param>
    /// <param name="input">Validated modify payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the refreshed DTO; 400/404/409 on failure.</returns>
    [HttpPut("api/executory-documents/{sqid}")]
    public async Task<ActionResult<ExecutoryDocumentDto>> ModifyAsync(
        string sqid,
        [FromBody] ExecutoryDocumentModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ModifyAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ExecutoryDocumentDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R1600 — flips an Active document to Suspended.</summary>
    /// <param name="sqid">Sqid-encoded document id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the refreshed DTO; 400/404/409 on failure.</returns>
    [HttpPost("api/executory-documents/{sqid}/suspend")]
    public async Task<ActionResult<ExecutoryDocumentDto>> SuspendAsync(
        string sqid,
        [FromBody] ExecutoryDocumentReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.SuspendAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ExecutoryDocumentDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R1600 — flips a Suspended document back to Active.</summary>
    /// <param name="sqid">Sqid-encoded document id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the refreshed DTO; 400/404/409 on failure.</returns>
    [HttpPost("api/executory-documents/{sqid}/resume")]
    public async Task<ActionResult<ExecutoryDocumentDto>> ResumeAsync(
        string sqid,
        [FromBody] ExecutoryDocumentReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ResumeAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ExecutoryDocumentDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R1600 — flips an Active / Suspended document to Cancelled.</summary>
    /// <param name="sqid">Sqid-encoded document id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the refreshed DTO; 400/404/409 on failure.</returns>
    [HttpPost("api/executory-documents/{sqid}/cancel")]
    public async Task<ActionResult<ExecutoryDocumentDto>> CancelAsync(
        string sqid,
        [FromBody] ExecutoryDocumentReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.CancelAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ExecutoryDocumentDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R1600 — manually completes a document (TotalWithheld must already meet TotalOwed unless open-ended).</summary>
    /// <param name="sqid">Sqid-encoded document id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the refreshed DTO; 400/404/409 on failure.</returns>
    [HttpPost("api/executory-documents/{sqid}/complete")]
    public async Task<ActionResult<ExecutoryDocumentDto>> CompleteAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.CompleteAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ExecutoryDocumentDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R1600 — fetches a single document by Sqid id.</summary>
    /// <param name="sqid">Sqid-encoded document id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the DTO when found; 404 otherwise.</returns>
    [HttpGet("api/executory-documents/{sqid}")]
    public async Task<ActionResult<ExecutoryDocumentDto>> GetAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ExecutoryDocumentDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R1600 — lists all executory documents on file for the supplied debtor IDNP.</summary>
    /// <param name="debtorIdnp">Plaintext IDNP (the service hashes internally for the lookup).</param>
    /// <param name="status">Optional status restriction (Active / Suspended / Completed / Cancelled).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the ordered list; 400 on bad input.</returns>
    [HttpGet("api/executory-documents")]
    public async Task<ActionResult<IReadOnlyList<ExecutoryDocumentDto>>> ListAsync(
        [FromQuery] string debtorIdnp,
        [FromQuery] string? status,
        CancellationToken cancellationToken = default)
    {
        ExecutoryDocumentStatusFilter? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ExecutoryDocumentStatus>(status, ignoreCase: false, out var parsed))
            {
                return Problem(
                    "status must be one of Active, Suspended, Completed, Cancelled.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            filter = new ExecutoryDocumentStatusFilter(parsed);
        }

        var result = await _svc.ListByDebtorAsync(debtorIdnp, filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<IReadOnlyList<ExecutoryDocumentDto>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps generic-result failures to ProblemDetails.</summary>
    /// <typeparam name="T">DTO type the action would have returned.</typeparam>
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
