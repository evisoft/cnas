using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Interop;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts.Interop;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers.Interop;

/// <summary>
/// R0634 / TOR CF 14.12 / Annex 4 — REST surface for the four canonical
/// machine-to-machine queries that other MGov systems (RSP, MoFin, IPS,
/// SIVE, SIAAS, ...) issue against CNAS. Every endpoint is gated by the
/// <c>InteropClient</c> role placeholder; real OAuth2 client-credentials
/// binding will replace it in a follow-up batch (see TODO §11 R1709).
/// </summary>
/// <remarks>
/// <para>
/// <b>POST-only, IDNP-in-body.</b> All four ops are POST endpoints — never
/// GET — so the IDNP travels in the request body and never surfaces in URL
/// paths, query strings, reverse-proxy access logs, or the CDN edge.
/// Surface logs do not become a secondary PII store.
/// </para>
/// <para>
/// <b>Rate limit policy.</b> The controller participates in the
/// <see cref="RateLimitingPolicies.Authenticated"/> policy. A dedicated
/// per-consumer policy lands once the OAuth2 client-credentials binding
/// names the actual subjects we throttle against — at that point the
/// policy can switch to a partition keyed on the client id rather than the
/// generic user-id key it uses today.
/// </para>
/// <para>
/// <b>Failure mapping.</b> 400 for validator / IDNP-shape failures;
/// 404 for unknown-citizen failures on the three ops that don't have a
/// soft-404 shape (<c>GetContributionHistory</c>, <c>GetBenefitsList</c>,
/// <c>GetPersonalAccountSnapshot</c>);
/// 200 for <c>GetInsuredPersonStatus</c> on both registered and
/// unregistered branches — that op is the one the consumer probes to
/// decide whether to call the other three.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "InteropClient")]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/interop")]
public sealed class InteropController : ControllerBase
{
    private readonly IInteropApi _api;
    private readonly InteropIdnpRequestDtoValidator _idnpValidator;
    private readonly InteropContributionHistoryRequestValidator _historyValidator;
    private readonly ActiveDecisionsRequestDtoValidator _activeDecisionsValidator;
    private readonly PaymentStatusRequestDtoValidator _paymentStatusValidator;
    private readonly PayerDataRequestDtoValidator _payerDataValidator;
    private readonly IsBenefitBeneficiaryRequestDtoValidator _isBeneficiaryValidator;
    private readonly ContributionPaymentInfoRequestDtoValidator _contributionPaymentInfoValidator;
    private readonly LegalApplicableFormRequestDtoValidator _legalFormValidator;

    /// <summary>Constructs the controller with its collaborators.</summary>
    /// <param name="api">Underlying Annex-4 façade.</param>
    /// <param name="idnpValidator">FluentValidation validator for the IDNP-only request envelope.</param>
    /// <param name="historyValidator">FluentValidation validator for the contribution-history envelope.</param>
    /// <param name="activeDecisionsValidator">FluentValidation validator for the active-decisions envelope (R1702).</param>
    /// <param name="paymentStatusValidator">FluentValidation validator for the payment-status envelope (R1703).</param>
    /// <param name="payerDataValidator">FluentValidation validator for the payer-data envelope (R1704).</param>
    /// <param name="isBeneficiaryValidator">FluentValidation validator for the is-beneficiary envelope (R1705).</param>
    /// <param name="contributionPaymentInfoValidator">FluentValidation validator for the contribution-payment-info envelope (R1706).</param>
    /// <param name="legalFormValidator">FluentValidation validator for the legal-applicable-form envelope (R1707).</param>
    public InteropController(
        IInteropApi api,
        InteropIdnpRequestDtoValidator idnpValidator,
        InteropContributionHistoryRequestValidator historyValidator,
        ActiveDecisionsRequestDtoValidator activeDecisionsValidator,
        PaymentStatusRequestDtoValidator paymentStatusValidator,
        PayerDataRequestDtoValidator payerDataValidator,
        IsBenefitBeneficiaryRequestDtoValidator isBeneficiaryValidator,
        ContributionPaymentInfoRequestDtoValidator contributionPaymentInfoValidator,
        LegalApplicableFormRequestDtoValidator legalFormValidator)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(idnpValidator);
        ArgumentNullException.ThrowIfNull(historyValidator);
        ArgumentNullException.ThrowIfNull(activeDecisionsValidator);
        ArgumentNullException.ThrowIfNull(paymentStatusValidator);
        ArgumentNullException.ThrowIfNull(payerDataValidator);
        ArgumentNullException.ThrowIfNull(isBeneficiaryValidator);
        ArgumentNullException.ThrowIfNull(contributionPaymentInfoValidator);
        ArgumentNullException.ThrowIfNull(legalFormValidator);
        _api = api;
        _idnpValidator = idnpValidator;
        _historyValidator = historyValidator;
        _activeDecisionsValidator = activeDecisionsValidator;
        _paymentStatusValidator = paymentStatusValidator;
        _payerDataValidator = payerDataValidator;
        _isBeneficiaryValidator = isBeneficiaryValidator;
        _contributionPaymentInfoValidator = contributionPaymentInfoValidator;
        _legalFormValidator = legalFormValidator;
    }

    /// <summary>
    /// R0634 / Annex 4 — <c>POST /api/interop/insured-person-status</c>.
    /// Returns 200 on both registered and unregistered branches (soft
    /// 404 shape) so probing callers do not have to wrap the call in
    /// try/catch.
    /// </summary>
    /// <param name="body">IDNP envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with <see cref="InsuredPersonStatusDto"/>; 400 on invalid body.</returns>
    [HttpPost("insured-person-status")]
    public async Task<ActionResult<InsuredPersonStatusDto>> GetInsuredPersonStatusAsync(
        [FromBody] InteropIdnpRequestDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var validation = await _idnpValidator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return MapValidationFailure(validation);
        }

        var result = await _api.GetInsuredPersonStatusAsync(body.Idnp, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0634 / Annex 4 — <c>POST /api/interop/contribution-history</c>.
    /// Returns 404 when the citizen is not on file.
    /// </summary>
    /// <param name="body">IDNP + window envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with <see cref="ContributionHistoryDto"/>; 400 on invalid body; 404 on unknown citizen.</returns>
    [HttpPost("contribution-history")]
    public async Task<ActionResult<ContributionHistoryDto>> GetContributionHistoryAsync(
        [FromBody] InteropContributionHistoryRequestDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var validation = await _historyValidator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return MapValidationFailure(validation);
        }

        var result = await _api
            .GetContributionHistoryAsync(body.Idnp, body.FromMonth, body.ToMonth, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0634 / Annex 4 — <c>POST /api/interop/benefits-list</c>.
    /// Returns 404 when the citizen is not on file.
    /// </summary>
    /// <param name="body">IDNP envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with <see cref="BenefitsListDto"/>; 400 on invalid body; 404 on unknown citizen.</returns>
    [HttpPost("benefits-list")]
    public async Task<ActionResult<BenefitsListDto>> GetBenefitsListAsync(
        [FromBody] InteropIdnpRequestDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var validation = await _idnpValidator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return MapValidationFailure(validation);
        }

        var result = await _api.GetBenefitsListAsync(body.Idnp, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0634 / Annex 4 — <c>POST /api/interop/personal-account-snapshot</c>.
    /// Returns 404 when the citizen has no personal-account aggregate.
    /// </summary>
    /// <param name="body">IDNP envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with <see cref="PersonalAccountSnapshotDto"/>; 400 on invalid body; 404 on missing account.</returns>
    [HttpPost("personal-account-snapshot")]
    public async Task<ActionResult<PersonalAccountSnapshotDto>> GetPersonalAccountSnapshotAsync(
        [FromBody] InteropIdnpRequestDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var validation = await _idnpValidator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return MapValidationFailure(validation);
        }

        var result = await _api.GetPersonalAccountSnapshotAsync(body.Idnp, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R1702 / Annex 4 — <c>POST /api/interop/active-decisions</c>. Returns
    /// 404 when the citizen is not on file; success otherwise (the decisions
    /// list may be empty for registered citizens with no active decisions).
    /// </summary>
    /// <param name="body">IDNP envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with <see cref="ActiveDecisionsDto"/>; 400 on invalid body; 404 on unknown citizen.</returns>
    [HttpPost("active-decisions")]
    public async Task<ActionResult<ActiveDecisionsDto>> GetActiveDecisionsAsync(
        [FromBody] ActiveDecisionsRequestDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var validation = await _activeDecisionsValidator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return MapValidationFailure(validation);
        }

        var result = await _api.GetActiveDecisionsAsync(body.Idnp, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R1703 / Annex 4 — <c>POST /api/interop/payment-status</c>. Returns
    /// 404 when the decision is not on file (or has no payment for the
    /// requested period).
    /// </summary>
    /// <param name="body">Decision Sqid + period envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with <see cref="PaymentStatusDto"/>; 400 on invalid body; 404 on missing data.</returns>
    [HttpPost("payment-status")]
    public async Task<ActionResult<PaymentStatusDto>> GetPaymentStatusAsync(
        [FromBody] PaymentStatusRequestDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var validation = await _paymentStatusValidator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return MapValidationFailure(validation);
        }

        var result = await _api
            .GetPaymentStatusAsync(body.DecisionSqid, body.Period, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R1704 / Annex 4 — <c>POST /api/interop/payer-data</c>. The
    /// <c>TaxpayerCode</c> may be IDNP (natural person) or IDNO (legal
    /// entity). Returns 404 when no payer is on file.
    /// </summary>
    /// <param name="body">TaxpayerCode envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with <see cref="PayerDataDto"/>; 400 on invalid body; 404 on unknown payer.</returns>
    [HttpPost("payer-data")]
    public async Task<ActionResult<PayerDataDto>> GetPayerDataAsync(
        [FromBody] PayerDataRequestDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var validation = await _payerDataValidator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return MapValidationFailure(validation);
        }

        var result = await _api.GetPayerDataAsync(body.TaxpayerCode, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R1705 / Annex 4 — <c>POST /api/interop/is-benefit-beneficiary</c>.
    /// Returns 200 on both true and false branches; 400 only on a malformed
    /// envelope.
    /// </summary>
    /// <param name="body">IDNP + BenefitType envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with <see cref="IsBenefitBeneficiaryDto"/>; 400 on invalid body.</returns>
    [HttpPost("is-benefit-beneficiary")]
    public async Task<ActionResult<IsBenefitBeneficiaryDto>> IsBenefitBeneficiaryAsync(
        [FromBody] IsBenefitBeneficiaryRequestDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var validation = await _isBeneficiaryValidator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return MapValidationFailure(validation);
        }

        var result = await _api
            .IsBenefitBeneficiaryAsync(body.Idnp, body.BenefitType, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R1706 / Annex 4 — <c>POST /api/interop/contribution-payment-info</c>.
    /// Returns 404 when the legal-entity payer is not on file.
    /// </summary>
    /// <param name="body">IDNO + period envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with <see cref="ContributionPaymentInfoDto"/>; 400 on invalid body; 404 on unknown payer.</returns>
    [HttpPost("contribution-payment-info")]
    public async Task<ActionResult<ContributionPaymentInfoDto>> GetContributionPaymentInfoAsync(
        [FromBody] ContributionPaymentInfoRequestDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var validation = await _contributionPaymentInfoValidator
            .ValidateAsync(body, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return MapValidationFailure(validation);
        }

        var result = await _api
            .GetContributionPaymentInfoAsync(body.Idno, body.Period, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R1707 / Annex 4 — <c>POST /api/interop/legal-applicable-form</c>.
    /// Returns 200 even on the <c>NotApplicable</c> branch — the consumer
    /// expects the boolean shape.
    /// </summary>
    /// <param name="body">IDNP + agreement-code envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with <see cref="LegalApplicableFormDto"/>; 400 on invalid body.</returns>
    [HttpPost("legal-applicable-form")]
    public async Task<ActionResult<LegalApplicableFormDto>> GetLegalApplicableFormAsync(
        [FromBody] LegalApplicableFormRequestDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var validation = await _legalFormValidator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return MapValidationFailure(validation);
        }

        var result = await _api
            .GetLegalApplicableFormAsync(body.Idnp, body.AgreementCode, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R1708 / Annex 4 — <c>POST /api/interop/work-insurance-period</c>.
    /// Returns 404 when the citizen is not on file.
    /// </summary>
    /// <param name="body">IDNP envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with <see cref="WorkInsurancePeriodDto"/>; 400 on invalid body; 404 on unknown citizen.</returns>
    [HttpPost("work-insurance-period")]
    public async Task<ActionResult<WorkInsurancePeriodDto>> GetWorkInsurancePeriodAsync(
        [FromBody] InteropIdnpRequestDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var validation = await _idnpValidator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return MapValidationFailure(validation);
        }

        var result = await _api.GetWorkInsurancePeriodAsync(body.Idnp, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Renders a FluentValidation failure as a 400 ProblemDetails with the
    /// first error message in the <c>detail</c> field and the canonical
    /// <see cref="ErrorCodes.ValidationFailed"/> in the
    /// <c>errorCode</c> extension.
    /// </summary>
    /// <param name="validation">FluentValidation result.</param>
    /// <returns>400 ProblemDetails ObjectResult.</returns>
    private ObjectResult MapValidationFailure(FluentValidation.Results.ValidationResult validation)
    {
        var first = validation.Errors[0].ErrorMessage;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Interop request rejected.",
            Detail = first,
        };
        problem.Extensions["errorCode"] = ErrorCodes.ValidationFailed;
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" },
        };
    }

    /// <summary>
    /// Maps a service-level <see cref="Result"/> failure to the appropriate
    /// HTTP status. <see cref="ErrorCodes.NotFound"/> → 404;
    /// <see cref="ErrorCodes.InvalidIdnp"/> /
    /// <see cref="ErrorCodes.InvalidDateRange"/> → 400; everything else →
    /// 400 as the safe default.
    /// </summary>
    /// <param name="errorCode">Stable error code from the service.</param>
    /// <param name="errorMessage">Human-readable message.</param>
    /// <returns>ProblemDetails ObjectResult with the mapped status.</returns>
    private ObjectResult MapFailure(string? errorCode, string? errorMessage)
    {
        var status = errorCode switch
        {
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            ErrorCodes.InvalidIdnp => StatusCodes.Status400BadRequest,
            ErrorCodes.InvalidIdno => StatusCodes.Status400BadRequest,
            ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
            ErrorCodes.InvalidDateRange => StatusCodes.Status400BadRequest,
            ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status400BadRequest,
        };

        var problem = new ProblemDetails
        {
            Status = status,
            Title = "Interop request rejected.",
            Detail = errorMessage,
        };
        problem.Extensions["errorCode"] = errorCode;
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
