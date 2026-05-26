using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Registers;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R1601 + R1602 / TOR Annex 3.9-3.10 — REST surface for the canonical
/// registers projection. Two endpoints:
/// <list type="bullet">
///   <item><c>GET /api/registers/decisions</c> — RegistrulDeciziilor.</item>
///   <item><c>GET /api/registers/payment-accounts</c> — RegistrulConturilorDePlata.</item>
/// </list>
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasUser)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/registers")]
public sealed class RegistersController : ControllerBase
{
    private readonly IDecisionsRegister _decisions;
    private readonly IBeneficiaryPaymentAccountsRegister _paymentAccounts;

    /// <summary>Constructs the controller with both register projections.</summary>
    /// <param name="decisions">Decisions register projection.</param>
    /// <param name="paymentAccounts">Payment-accounts register projection.</param>
    public RegistersController(
        IDecisionsRegister decisions,
        IBeneficiaryPaymentAccountsRegister paymentAccounts)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(paymentAccounts);
        _decisions = decisions;
        _paymentAccounts = paymentAccounts;
    }

    /// <summary>R1601 — list rows of the RegistrulDeciziilor projection.</summary>
    /// <param name="from">Optional inclusive lower bound on issuance UTC.</param>
    /// <param name="to">Optional exclusive upper bound on issuance UTC.</param>
    /// <param name="type">Optional stable type code (e.g. <c>DECIZIE_RECUPERARE_SUME</c>).</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Items per page (clamped to [1, 200]).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the paged result, or 400 on a malformed window.</returns>
    [HttpGet("decisions")]
    public async Task<ActionResult<PagedResult<DecisionRegisterRowDto>>> ListDecisionsAsync(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? type = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _decisions.ListAsync(
            new DecisionRegisterFilter(from, to, type),
            page,
            pageSize,
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>R1602 — list rows of the RegistrulConturilorDePlata projection.</summary>
    /// <remarks>
    /// <para>
    /// <b>PII channel — header, not query string.</b> The optional IDNP filter is
    /// accepted via the <c>X-Beneficiary-Idnp</c> request header rather than a
    /// <c>?beneficiaryIdnp=</c> query string parameter. Query strings are routinely
    /// captured in server logs, reverse-proxy access logs, browser history, and
    /// HTTP referrer headers; surfacing a Moldovan national identifier through
    /// any of those channels would leak PII to logs that were not designed to
    /// carry it. Request headers are not logged by default by the reverse proxy
    /// fronting CNAS, which keeps the identifier out of the routine log corpus.
    /// </para>
    /// </remarks>
    /// <param name="beneficiaryIdnp">Optional raw IDNP filter via the <c>X-Beneficiary-Idnp</c> header.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Items per page (clamped to [1, 200]).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the paged result.</returns>
    [HttpGet("payment-accounts")]
    public async Task<ActionResult<PagedResult<BeneficiaryPaymentAccountRowDto>>> ListPaymentAccountsAsync(
        [FromHeader(Name = "X-Beneficiary-Idnp")] string? beneficiaryIdnp = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _paymentAccounts.ListAsync(
            beneficiaryIdnp,
            page,
            pageSize,
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }
}
