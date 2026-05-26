using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Declarations;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0810 / R0811 / R0812 / R0813 / TOR BP 1.2 (Annex 8 — Declarații) — REST
/// surface for the contribution-declarations registry. Three POST endpoints
/// expose the three registration paths (SFS feed, CNAS desk, other documents);
/// two POST endpoints expose the per-row adjust / cancel lifecycle; one GET
/// endpoint lists declarations for a payer.
/// </summary>
[ApiController]
[Authorize(Roles = "cnas-admin,cnas-user")]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
public sealed class DeclarationsController : ControllerBase
{
    private readonly IDeclarationService _svc;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the controller with its collaborators.</summary>
    /// <param name="svc">Declaration service façade.</param>
    /// <param name="sqids">Sqid encoder/decoder for route parameters.</param>
    public DeclarationsController(IDeclarationService svc, ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(sqids);
        _svc = svc;
        _sqids = sqids;
    }

    /// <summary>R0810 / BP 1.2-A — register a declaration ingested from the SFS feed.</summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// 201 with the new <see cref="DeclarationDto"/>; 400/404/409 ProblemDetails on failure.
    /// </returns>
    [HttpPost("api/declarations/sfs")]
    public async Task<ActionResult<DeclarationDto>> RegisterFromSfsAsync(
        [FromBody] DeclarationFromSfsInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.RegisterFromSfsAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? CreatedAtAction(nameof(ListForPayerAsync), new { contributorSqid = result.Value.ContributorSqid }, result.Value)
            : MapFailureGeneric<DeclarationDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0811 / BP 1.2-B — register a paper declaration submitted at a CNAS desk.</summary>
    /// <param name="input">Validated input envelope (Kind constrained by validator).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>201 on success; 400/404/409 on failure.</returns>
    [HttpPost("api/declarations/cnas-desk")]
    public async Task<ActionResult<DeclarationDto>> RegisterAtCnasAsync(
        [FromBody] DeclarationAtCnasInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.RegisterAtCnasAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? CreatedAtAction(nameof(ListForPayerAsync), new { contributorSqid = result.Value.ContributorSqid }, result.Value)
            : MapFailureGeneric<DeclarationDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0812 / BP 1.2-C — register a contribution recalculated from another document.</summary>
    /// <param name="input">Validated input envelope (Kind constrained by validator).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>201 on success; 400/404/409 on failure.</returns>
    [HttpPost("api/declarations/other-document")]
    public async Task<ActionResult<DeclarationDto>> RegisterFromOtherDocumentAsync(
        [FromBody] DeclarationFromOtherDocumentInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.RegisterFromOtherDocumentAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? CreatedAtAction(nameof(ListForPayerAsync), new { contributorSqid = result.Value.ContributorSqid }, result.Value)
            : MapFailureGeneric<DeclarationDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Adjusts an existing declaration with a superseding amount.</summary>
    /// <param name="sqid">Sqid-encoded declaration id.</param>
    /// <param name="input">Adjustment payload (new amount + reason).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the updated DTO; 400/404/409 on failure.</returns>
    [HttpPost("api/declarations/{sqid}/adjust")]
    public async Task<ActionResult<DeclarationDto>> AdjustAsync(
        string sqid,
        [FromBody] DeclarationAdjustInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<DeclarationDto>(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.AdjustAsync(decoded.Value, input.AdjustedAmount, input.Reason, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<DeclarationDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Cancels an existing declaration. Cancelled rows are excluded from R0813 totals.</summary>
    /// <param name="sqid">Sqid-encoded declaration id.</param>
    /// <param name="input">Cancellation payload (reason).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>204 on success; 400/404/409 on failure.</returns>
    [HttpPost("api/declarations/{sqid}/cancel")]
    public async Task<IActionResult> CancelAsync(
        string sqid,
        [FromBody] DeclarationCancelInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureBare(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.CancelAsync(decoded.Value, input.Reason, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Lists declarations for a payer inside the supplied month window.</summary>
    /// <param name="contributorSqid">Sqid-encoded payer id.</param>
    /// <param name="fromMonth">Inclusive lower bound (day = 1).</param>
    /// <param name="toMonth">Inclusive upper bound (day = 1).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the ordered list; 400 on bad Sqid.</returns>
    [HttpGet("api/declarations/payer/{contributorSqid}")]
    public async Task<ActionResult<IReadOnlyList<DeclarationDto>>> ListForPayerAsync(
        string contributorSqid,
        [FromQuery] DateOnly fromMonth,
        [FromQuery] DateOnly toMonth,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(contributorSqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<IReadOnlyList<DeclarationDto>>(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var list = await _svc.ListForPayerAsync(decoded.Value, fromMonth, toMonth, cancellationToken)
            .ConfigureAwait(false);
        return Ok(list);
    }

    /// <summary>
    /// R0821 / BP 1.2-L — attaches a scanned copy of the paper declaration
    /// plus optional OCR metadata to an existing row.
    /// </summary>
    /// <param name="sqid">Sqid-encoded declaration id.</param>
    /// <param name="input">Scanned-copy upload envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// 200 with the refreshed <see cref="DeclarationDto"/>; 400 on bad Sqid /
    /// validation failure; 404 on missing declaration; 409 on cancelled row.
    /// </returns>
    [HttpPost("api/declarations/{sqid}/scanned-copy")]
    public async Task<ActionResult<DeclarationDto>> AttachScannedCopyAsync(
        string sqid,
        [FromBody] ScannedDeclarationAttachmentInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<DeclarationDto>(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.AttachScannedCopyAsync(decoded.Value, input, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<DeclarationDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0822 / BP 1.2-M — server-side paged + budget-gated explorer endpoint
    /// for the Declarations registry. Accepts the QBE filter + paging in the
    /// request body.
    /// </summary>
    /// <param name="input">QBE-driven search input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// 200 with the populated <see cref="DeclarationsListPageDto"/>; 400 on
    /// validation / QBE failure; 400 (carrying the QUERY_TOO_BROAD message)
    /// when the budget gate refuses.
    /// </returns>
    [HttpPost("api/declarations/search")]
    public async Task<ActionResult<DeclarationsListPageDto>> SearchAsync(
        [FromBody] DeclarationsSearchInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var result = await _svc.SearchAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<DeclarationsListPageDto>(result.ErrorCode, result.ErrorMessage);
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
    /// <returns>404 / 409 / 403 / 501 / 400 as appropriate.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Conflict => StatusCodes.Status409Conflict,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.NotImplemented => StatusCodes.Status501NotImplemented,
        _ => StatusCodes.Status400BadRequest,
    };
}

/// <summary>
/// R0813 / BP 1.2-D — monthly contribution-calculation endpoint. Mounted on
/// the contributors path because the aggregator is per-payer.
/// </summary>
[ApiController]
[Authorize(Roles = "cnas-admin,cnas-user")]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
public sealed class MonthlyContributionCalculationController : ControllerBase
{
    private readonly IMonthlyContributionCalculator _calculator;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the controller with its collaborators.</summary>
    /// <param name="calculator">Monthly contribution-calculator service.</param>
    /// <param name="sqids">Sqid encoder/decoder for the route parameter.</param>
    public MonthlyContributionCalculationController(
        IMonthlyContributionCalculator calculator,
        ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(calculator);
        ArgumentNullException.ThrowIfNull(sqids);
        _calculator = calculator;
        _sqids = sqids;
    }

    /// <summary>
    /// R0813 / BP 1.2-D — recompute the per-payer per-month roll-up. Idempotent
    /// on the (contributor, month) natural key.
    /// </summary>
    /// <param name="contributorSqid">Sqid-encoded payer id.</param>
    /// <param name="month">Calendar month (day = 1).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the populated DTO; 400 on bad Sqid/month; 404 on missing payer.</returns>
    [HttpPost("api/contributors/{contributorSqid}/monthly-contribution/{month}/calculate")]
    public async Task<ActionResult<MonthlyContributionCalculationDto>> CalculateAsync(
        string contributorSqid,
        DateOnly month,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(contributorSqid);
        if (decoded.IsFailure)
        {
            return MapFailure(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _calculator.CalculateAsync(decoded.Value, month, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps the service-level failure to ProblemDetails.</summary>
    /// <param name="code">Stable error code from the service.</param>
    /// <param name="message">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ActionResult<MonthlyContributionCalculationDto> MapFailure(string? code, string? message)
    {
        var status = code switch
        {
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            ErrorCodes.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }
}
