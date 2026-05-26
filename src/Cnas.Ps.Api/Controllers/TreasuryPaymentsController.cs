using Cnas.Ps.Application.Treasury;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0911 / TOR BP 2.2-B — REST surface for the Treasury payment-receipt
/// registry. Three endpoints: import a single receipt, trigger per-receipt
/// distribution, and fetch a receipt by id. The background
/// <c>TreasuryDistributionJob</c> drains all <c>Pending</c> receipts every
/// 15 minutes automatically — the distribute endpoint is the operator-driven
/// re-trigger path.
/// </summary>
[ApiController]
[Authorize(Roles = "cnas-admin,cnas-user")]
public sealed class TreasuryPaymentsController : ControllerBase
{
    private readonly ITreasuryPaymentService _svc;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the controller with its collaborators.</summary>
    /// <param name="svc">Treasury payment service façade.</param>
    /// <param name="sqids">Sqid encoder/decoder for route parameters.</param>
    public TreasuryPaymentsController(
        ITreasuryPaymentService svc,
        ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(sqids);
        _svc = svc;
        _sqids = sqids;
    }

    /// <summary>R0911 / BP 2.2-B — import a single Treasury payment receipt in Pending state.</summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>201 with the persisted DTO; 400/404 on failure.</returns>
    [HttpPost("api/treasury-payments/import")]
    public async Task<ActionResult<TreasuryPaymentReceiptDto>> ImportAsync(
        [FromBody] TreasuryPaymentReceiptImportInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ImportReceiptAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : MapFailureGeneric<TreasuryPaymentReceiptDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0911 / BP 2.2-B — distribute a Pending receipt across matching REV-5 rows.</summary>
    /// <param name="sqid">Sqid-encoded receipt id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the refreshed DTO at terminal status; 400/404 on failure.</returns>
    [HttpPost("api/treasury-payments/{sqid}/distribute")]
    public async Task<ActionResult<TreasuryPaymentReceiptDto>> DistributeAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<TreasuryPaymentReceiptDto>(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var result = await _svc.DistributeAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<TreasuryPaymentReceiptDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0911 — fetch a single Treasury payment receipt by surrogate id.</summary>
    /// <param name="sqid">Sqid-encoded receipt id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the DTO when found; 404 otherwise.</returns>
    [HttpGet("api/treasury-payments/{sqid}")]
    public async Task<ActionResult<TreasuryPaymentReceiptDto>> GetAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<TreasuryPaymentReceiptDto>(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var result = await _svc.GetAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<TreasuryPaymentReceiptDto>(result.ErrorCode, result.ErrorMessage);
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
