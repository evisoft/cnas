using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R1000..R1034 / TOR §3.2-AB..AD — admin REST surface over the
/// voucher-quota engine that gates the spa / rehabilitation / sanatorium
/// passports. Read endpoints are open to authenticated callers; configure
/// is restricted to <see cref="AuthorizationComposition.CnasAdmin"/>.
/// </summary>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/voucher-quotas")]
public sealed class VoucherQuotasController(IVoucherQuotaService service) : ControllerBase
{
    private readonly IVoucherQuotaService _service = service;

    /// <summary>Reads the current availability snapshot for the given (passport, year, month).</summary>
    /// <param name="passportCode">Stable passport code (e.g. <c>3.2-AB</c>).</param>
    /// <param name="year">Calendar year.</param>
    /// <param name="month">Month-of-year (1..12).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the snapshot; 400/404 on failure.</returns>
    [HttpGet("{passportCode}/{year:int}/{month:int}")]
    public async Task<ActionResult<VoucherQuotaCheckDto>> GetAvailabilityAsync(
        string passportCode,
        int year,
        int month,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.CheckAvailabilityAsync(passportCode, year, month, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<VoucherQuotaCheckDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Operator-facing seed / upsert of a voucher quota for the given (passport, year) tuple.</summary>
    /// <param name="passportCode">Stable passport code.</param>
    /// <param name="year">Calendar year.</param>
    /// <param name="input">Caps payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the persisted quota DTO; 400 on validation failure.</returns>
    [HttpPost("{passportCode}/{year:int}/configure")]
    [Authorize(Policy = AuthorizationComposition.CnasAdmin)]
    [Consumes("application/json")]
    public async Task<ActionResult<VoucherQuotaDto>> ConfigureAsync(
        string passportCode,
        int year,
        [FromBody] VoucherQuotaConfigureInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ConfigureQuotaAsync(
                passportCode, year, input.MonthlyQuota, input.AnnualQuota, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<VoucherQuotaDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Translates a failed <see cref="Result{T}"/> into the appropriate
    /// <see cref="ActionResult"/>: invalid sqid / validation → 400,
    /// not-configured → 404, quota-exhausted → 409, anything else → 500.
    /// </summary>
    /// <typeparam name="T">DTO type that would have been returned on success.</typeparam>
    /// <param name="errorCode">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="errorMessage">Human-readable description.</param>
    /// <returns>An <see cref="ActionResult{T}"/> carrying the appropriate HTTP status.</returns>
    private ActionResult<T> MapFailure<T>(string errorCode, string errorMessage)
        => errorCode switch
        {
            ErrorCodes.InvalidSqid => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.ValidationFailed => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            IVoucherQuotaService.QuotaNotConfiguredCode => NotFound(new { error = errorCode, message = errorMessage }),
            IVoucherQuotaService.QuotaExhaustedCode => Conflict(new { error = errorCode, message = errorMessage }),
            IVoucherQuotaService.QuotaUnderflowCode => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
