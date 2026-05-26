using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.IntlAgreements;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R1201 / R1402 / TOR §3.4-B / §3.6-C — REST surface over the
/// international-agreements 3-level routing registry. Restricted to the
/// <see cref="AuthorizationComposition.CnasAdmin"/> policy because every
/// transition touches sensitive PII (beneficiary IDNP, agreement codes,
/// cross-border claims). The per-level reviewer-role check happens at the
/// service layer, NOT here — controller authorisation only gates the
/// umbrella admin policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST /api/intl-agreement-cases</c> — create a case in <c>Draft</c>.</item>
///   <item><c>POST /api/intl-agreement-cases/{sqid}/submit</c> — Draft → AtLocalReview.</item>
///   <item><c>POST /api/intl-agreement-cases/{sqid}/review</c> — record decision at current level.</item>
///   <item><c>POST /api/intl-agreement-cases/{sqid}/resubmit</c> — re-enter chain from level 1.</item>
///   <item><c>POST /api/intl-agreement-cases/{sqid}/cancel</c> — operator cancel with reason.</item>
///   <item><c>GET  /api/intl-agreement-cases/{sqid}</c> — fetch single case.</item>
///   <item><c>GET  /api/intl-agreement-cases?…</c> — paginated list with filters.</item>
/// </list>
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/intl-agreement-cases")]
public sealed class IntlAgreementReviewCasesController : ControllerBase
{
    private readonly IIntlAgreementRoutingService _service;

    /// <summary>Constructs the controller with its scoped collaborator.</summary>
    /// <param name="service">Routing service façade.</param>
    public IntlAgreementReviewCasesController(IIntlAgreementRoutingService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <summary>POST <c>/api/intl-agreement-cases</c> — create a case in <c>Draft</c>.</summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>201 with the created DTO; 400 / 401 / 409 on failure.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<IntlAgreementReviewCaseDto>> CreateAsync(
        [FromBody] IntlAgreementReviewCaseCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Created($"/api/intl-agreement-cases/{result.Value.Id}", result.Value)
            : MapFailure<IntlAgreementReviewCaseDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/intl-agreement-cases/{sqid}/submit</c> — Draft → AtLocalReview.</summary>
    /// <param name="sqid">Sqid-encoded case id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated DTO; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/submit")]
    public async Task<ActionResult<IntlAgreementReviewCaseDto>> SubmitAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.SubmitAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<IntlAgreementReviewCaseDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/intl-agreement-cases/{sqid}/review</c> — record decision at current level.</summary>
    /// <param name="sqid">Sqid-encoded case id.</param>
    /// <param name="input">Review-decision envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated DTO; 400 / 401 / 403 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/review")]
    [Consumes("application/json")]
    public async Task<ActionResult<IntlAgreementReviewCaseDto>> RecordReviewAsync(
        string sqid,
        [FromBody] IntlAgreementReviewInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.RecordReviewAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<IntlAgreementReviewCaseDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/intl-agreement-cases/{sqid}/resubmit</c> — re-enter chain from level 1.</summary>
    /// <param name="sqid">Sqid-encoded case id.</param>
    /// <param name="input">Re-submit envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated DTO; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/resubmit")]
    [Consumes("application/json")]
    public async Task<ActionResult<IntlAgreementReviewCaseDto>> ResubmitAsync(
        string sqid,
        [FromBody] IntlAgreementReviewCaseResubmitInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ResubmitAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<IntlAgreementReviewCaseDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/intl-agreement-cases/{sqid}/cancel</c> — operator cancel with reason.</summary>
    /// <param name="sqid">Sqid-encoded case id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated DTO; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/cancel")]
    [Consumes("application/json")]
    public async Task<ActionResult<IntlAgreementReviewCaseDto>> CancelAsync(
        string sqid,
        [FromBody] IntlAgreementReviewCaseReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CancelAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<IntlAgreementReviewCaseDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>GET <c>/api/intl-agreement-cases/{sqid}</c> — fetch single case.</summary>
    /// <param name="sqid">Sqid-encoded case id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the DTO; 400 / 401 / 404 on failure.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<IntlAgreementReviewCaseDto>> GetAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<IntlAgreementReviewCaseDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>GET <c>/api/intl-agreement-cases</c> — paginated list with filters.</summary>
    /// <param name="status">Optional lifecycle status filter.</param>
    /// <param name="benefitKind">Optional benefit-kind filter.</param>
    /// <param name="agreementCode">Optional agreement-code filter.</param>
    /// <param name="hostCountryCode">Optional host-country-code filter.</param>
    /// <param name="currentLevel">Optional current-level filter.</param>
    /// <param name="skip">Page offset (≥ 0).</param>
    /// <param name="take">Page size (1..100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the page DTO; 400 on validation failure.</returns>
    [HttpGet]
    public async Task<ActionResult<IntlAgreementReviewCasePageDto>> ListAsync(
        [FromQuery] string? status = null,
        [FromQuery] string? benefitKind = null,
        [FromQuery] string? agreementCode = null,
        [FromQuery] string? hostCountryCode = null,
        [FromQuery] string? currentLevel = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 25,
        CancellationToken cancellationToken = default)
    {
        var filter = new IntlAgreementReviewCaseFilterDto(
            Status: status,
            BenefitKind: benefitKind,
            AgreementCode: agreementCode,
            HostCountryCode: hostCountryCode,
            CurrentLevel: currentLevel,
            Skip: skip,
            Take: take);
        var result = await _service.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<IntlAgreementReviewCasePageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Translates a failed <see cref="Result{T}"/> into the appropriate
    /// <see cref="ActionResult"/>.
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
            ErrorCodes.Unauthorized => Unauthorized(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Forbidden => StatusCode(StatusCodes.Status403Forbidden,
                new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(StatusCodes.Status500InternalServerError,
                new { error = errorCode, message = errorMessage }),
        };
}
