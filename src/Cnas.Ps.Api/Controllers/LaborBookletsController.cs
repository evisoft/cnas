using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.LaborBooklet;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0920 / R0921 / TOR BP 2.3 — REST surface for the labor-booklet
/// (Carnet de muncă) registry + its child pre-01.01.1999 activity periods.
/// </summary>
[ApiController]
[Authorize(Roles = "cnas-admin,cnas-user")]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
public sealed class LaborBookletsController : ControllerBase
{
    private readonly ILaborBookletService _svc;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the controller with its collaborators.</summary>
    /// <param name="svc">Labor-booklet service façade.</param>
    /// <param name="sqids">Sqid encoder/decoder for route parameters.</param>
    public LaborBookletsController(ILaborBookletService svc, ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(sqids);
        _svc = svc;
        _sqids = sqids;
    }

    /// <summary>R0920 / BP 2.3-A — register a fresh labor-booklet master row.</summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>201 with the new <see cref="LaborBookletDto"/>; 400/404/409 on failure.</returns>
    [HttpPost("api/labor-booklets")]
    public async Task<ActionResult<LaborBookletDto>> RegisterAsync(
        [FromBody] LaborBookletRegisterInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.RegisterAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetAsync), new { sqid = result.Value.Id }, result.Value)
            : MapFailureGeneric<LaborBookletDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0920 / BP 2.3-A — attach a scanned copy + optional OCR metadata.</summary>
    /// <param name="sqid">Sqid-encoded booklet id.</param>
    /// <param name="input">Upload envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the refreshed DTO; 400/404/409 on failure.</returns>
    [HttpPost("api/labor-booklets/{sqid}/scanned-copy")]
    public async Task<ActionResult<LaborBookletDto>> AttachScannedCopyAsync(
        string sqid,
        [FromBody] ScannedCopyAttachmentInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<LaborBookletDto>(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.AttachScannedCopyAsync(decoded.Value, input, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<LaborBookletDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0920 / BP 2.3-A — verify a Pending labor booklet.</summary>
    /// <param name="sqid">Sqid-encoded booklet id.</param>
    /// <param name="input">Verifier input envelope (optional notes).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the updated DTO; 400/404/409 on failure.</returns>
    [HttpPost("api/labor-booklets/{sqid}/verify")]
    public async Task<ActionResult<LaborBookletDto>> VerifyAsync(
        string sqid,
        [FromBody] LaborBookletVerifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<LaborBookletDto>(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.VerifyAsync(decoded.Value, input.Notes, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<LaborBookletDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0920 / BP 2.3-A — reject a Pending labor booklet.</summary>
    /// <param name="sqid">Sqid-encoded booklet id.</param>
    /// <param name="input">Rejection input envelope (reason required).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the updated DTO; 400/404/409 on failure.</returns>
    [HttpPost("api/labor-booklets/{sqid}/reject")]
    public async Task<ActionResult<LaborBookletDto>> RejectAsync(
        string sqid,
        [FromBody] LaborBookletRejectInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<LaborBookletDto>(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.RejectAsync(decoded.Value, input.Reason, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<LaborBookletDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0921 / BP 2.3-B — add a pre-1999 activity period to the booklet.</summary>
    /// <param name="sqid">Sqid-encoded booklet id.</param>
    /// <param name="input">Period payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>204 on success; 400/404/409 on failure.</returns>
    [HttpPost("api/labor-booklets/{sqid}/periods")]
    public async Task<IActionResult> AddPeriodAsync(
        string sqid,
        [FromBody] InsuredPersonPre1999PeriodInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureBare(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.AddPeriodAsync(decoded.Value, input, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0921 / BP 2.3-B — amend an existing pre-1999 activity period.</summary>
    /// <param name="sqid">Sqid-encoded period id.</param>
    /// <param name="input">Replacement payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>204 on success; 400/404/409 on failure.</returns>
    [HttpPut("api/insured-person-pre1999-periods/{sqid}")]
    public async Task<IActionResult> AmendPeriodAsync(
        string sqid,
        [FromBody] InsuredPersonPre1999PeriodInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureBare(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.AmendPeriodAsync(decoded.Value, input, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0921 / BP 2.3-B — close an existing pre-1999 activity period.</summary>
    /// <param name="sqid">Sqid-encoded period id.</param>
    /// <param name="input">Close payload (reason required).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>204 on success; 400/404/409 on failure.</returns>
    [HttpDelete("api/insured-person-pre1999-periods/{sqid}")]
    public async Task<IActionResult> ClosePeriodAsync(
        string sqid,
        [FromBody] LaborBookletRejectInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureBare(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.ClosePeriodAsync(decoded.Value, input.Reason, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Fetches a single labor booklet by Sqid id.</summary>
    /// <param name="sqid">Sqid-encoded booklet id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the DTO; 404 when missing; 400 on bad Sqid.</returns>
    [HttpGet("api/labor-booklets/{sqid}")]
    public async Task<ActionResult<LaborBookletDto>> GetAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<LaborBookletDto>(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var dto = await _svc.GetAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Lists every active pre-1999 period for the insured person.</summary>
    /// <param name="insuredPersonSqid">Sqid-encoded natural-person Solicitant id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the ordered list; 400 on bad Sqid.</returns>
    [HttpGet("api/insured-persons/{insuredPersonSqid}/pre1999-periods")]
    public async Task<ActionResult<IReadOnlyList<InsuredPersonPre1999PeriodDto>>> ListPeriodsAsync(
        string insuredPersonSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(insuredPersonSqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<IReadOnlyList<InsuredPersonPre1999PeriodDto>>(
                decoded.ErrorCode, decoded.ErrorMessage);
        }

        var list = await _svc.ListPeriodsForInsuredPersonAsync(decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return Ok(list);
    }

    /// <summary>Maps generic-result failures to ProblemDetails.</summary>
    /// <typeparam name="T">DTO type the action would have returned.</typeparam>
    /// <param name="code">Stable error code from the service.</param>
    /// <param name="message">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ActionResult<T> MapFailureGeneric<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Maps bare-result failures to ProblemDetails.</summary>
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
