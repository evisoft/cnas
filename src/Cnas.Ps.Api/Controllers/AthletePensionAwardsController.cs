using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.AthletePensions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R1403 / TOR §3.6-D — REST surface over the lifetime athlete-pension
/// registry. Restricted to the <see cref="AuthorizationComposition.CnasAdmin"/>
/// policy because athlete-pension lifecycle transitions touch sensitive
/// financial data (monthly amounts, beneficiary IDNP).
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST /api/athlete-pensions</c> — create an award in <c>Draft</c>.</item>
///   <item><c>POST /api/athlete-pensions/{sqid}/career-records</c> — add a record.</item>
///   <item><c>POST /api/athlete-pensions/{sqid}/career-records/{recordSqid}/verify</c> — verify a record.</item>
///   <item><c>POST /api/athlete-pensions/{sqid}/submit</c> — transition <c>Draft</c> → <c>Submitted</c>.</item>
///   <item><c>GET  /api/athlete-pensions/{sqid}/eligibility</c> — evaluate eligibility (no state change).</item>
///   <item><c>POST /api/athlete-pensions/{sqid}/approve</c> — approve a submitted award.</item>
///   <item><c>POST /api/athlete-pensions/{sqid}/reject</c> — reject a submitted award.</item>
///   <item><c>POST /api/athlete-pensions/{sqid}/activate</c> — activate an approved award.</item>
///   <item><c>POST /api/athlete-pensions/{sqid}/suspend</c> — suspend an active award.</item>
///   <item><c>POST /api/athlete-pensions/{sqid}/resume</c> — resume a suspended award.</item>
///   <item><c>POST /api/athlete-pensions/{sqid}/terminate</c> — terminate a non-terminal award.</item>
///   <item><c>GET  /api/athlete-pensions/{sqid}</c> — fetch a single award.</item>
///   <item><c>GET  /api/athlete-pensions?status=…&amp;role=…&amp;skip=…&amp;take=…</c> — list awards.</item>
/// </list>
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/athlete-pensions")]
public sealed class AthletePensionAwardsController : ControllerBase
{
    private readonly IAthletePensionAwardService _service;

    /// <summary>Constructs the controller with its scoped collaborator.</summary>
    /// <param name="service">Athlete-pension service façade.</param>
    public AthletePensionAwardsController(IAthletePensionAwardService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <summary>POST <c>/api/athlete-pensions</c> — create an award in <c>Draft</c>.</summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>201 with the created DTO; 400 / 401 / 409 on failure.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<AthletePensionAwardDto>> CreateAsync(
        [FromBody] AthletePensionAwardCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Created($"/api/athlete-pensions/{result.Value.Id}", result.Value)
            : MapFailure<AthletePensionAwardDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/athlete-pensions/{sqid}/career-records</c> — add a career-record row.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="input">Career-record input envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the refreshed award; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/career-records")]
    [Consumes("application/json")]
    public async Task<ActionResult<AthletePensionAwardDto>> AddCareerRecordAsync(
        string sqid,
        [FromBody] AthleteCareerRecordInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.AddCareerRecordAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<AthletePensionAwardDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/athlete-pensions/{sqid}/career-records/{recordSqid}/verify</c> — verify a record.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="recordSqid">Sqid-encoded record id.</param>
    /// <param name="input">Verification envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the refreshed award; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/career-records/{recordSqid}/verify")]
    [Consumes("application/json")]
    public async Task<ActionResult<AthletePensionAwardDto>> VerifyCareerRecordAsync(
        string sqid,
        string recordSqid,
        [FromBody] AthleteCareerRecordVerificationInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.VerifyCareerRecordAsync(sqid, recordSqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<AthletePensionAwardDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/athlete-pensions/{sqid}/submit</c> — transition <c>Draft</c> → <c>Submitted</c>.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated DTO; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/submit")]
    public async Task<ActionResult<AthletePensionAwardDto>> SubmitAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.SubmitAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<AthletePensionAwardDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>GET <c>/api/athlete-pensions/{sqid}/eligibility</c> — evaluate eligibility (no state change).</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the verdict; 400 / 401 / 404 on failure.</returns>
    [HttpGet("{sqid}/eligibility")]
    public async Task<ActionResult<AthletePensionEligibilityVerdictDto>> EvaluateEligibilityAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.EvaluateEligibilityAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<AthletePensionEligibilityVerdictDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/athlete-pensions/{sqid}/approve</c> — approve a submitted award.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="input">Approval envelope (mandatory note + regulatory base).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated DTO; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/approve")]
    [Consumes("application/json")]
    public async Task<ActionResult<AthletePensionAwardDto>> ApproveAsync(
        string sqid,
        [FromBody] AthletePensionApprovalInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ApproveAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<AthletePensionAwardDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/athlete-pensions/{sqid}/reject</c> — reject a submitted award.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated DTO; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/reject")]
    [Consumes("application/json")]
    public async Task<ActionResult<AthletePensionAwardDto>> RejectAsync(
        string sqid,
        [FromBody] AthletePensionReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.RejectAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<AthletePensionAwardDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/athlete-pensions/{sqid}/activate</c> — activate an approved award.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="input">Activation envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated DTO; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/activate")]
    [Consumes("application/json")]
    public async Task<ActionResult<AthletePensionAwardDto>> ActivateAsync(
        string sqid,
        [FromBody] AthletePensionActivationInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ActivateAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<AthletePensionAwardDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/athlete-pensions/{sqid}/suspend</c> — suspend an active award.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated DTO; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/suspend")]
    [Consumes("application/json")]
    public async Task<ActionResult<AthletePensionAwardDto>> SuspendAsync(
        string sqid,
        [FromBody] AthletePensionReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.SuspendAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<AthletePensionAwardDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/athlete-pensions/{sqid}/resume</c> — resume a suspended award.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated DTO; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/resume")]
    [Consumes("application/json")]
    public async Task<ActionResult<AthletePensionAwardDto>> ResumeAsync(
        string sqid,
        [FromBody] AthletePensionReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ResumeAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<AthletePensionAwardDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>POST <c>/api/athlete-pensions/{sqid}/terminate</c> — terminate a non-terminal award.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated DTO; 400 / 401 / 404 / 409 on failure.</returns>
    [HttpPost("{sqid}/terminate")]
    [Consumes("application/json")]
    public async Task<ActionResult<AthletePensionAwardDto>> TerminateAsync(
        string sqid,
        [FromBody] AthletePensionReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await _service.TerminateAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<AthletePensionAwardDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>GET <c>/api/athlete-pensions/{sqid}</c> — fetch a single award.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the DTO; 400 / 401 / 404 on failure.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<AthletePensionAwardDto>> GetAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<AthletePensionAwardDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>GET <c>/api/athlete-pensions</c> — list awards with filters.</summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="role">Optional role filter.</param>
    /// <param name="sportDiscipline">Optional sport-discipline filter.</param>
    /// <param name="beneficiaryIdnpHash">Optional beneficiary IDNP hash filter.</param>
    /// <param name="skip">Page offset (≥ 0).</param>
    /// <param name="take">Page size (1..100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the page DTO; 400 on validation failure.</returns>
    [HttpGet]
    public async Task<ActionResult<AthletePensionAwardPageDto>> ListAsync(
        [FromQuery] string? status = null,
        [FromQuery] string? role = null,
        [FromQuery] string? sportDiscipline = null,
        [FromQuery] string? beneficiaryIdnpHash = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 25,
        CancellationToken cancellationToken = default)
    {
        var filter = new AthletePensionAwardFilterDto(
            Status: status,
            Role: role,
            SportDiscipline: sportDiscipline,
            BeneficiaryIdnpHash: beneficiaryIdnpHash,
            Skip: skip,
            Take: take);
        var result = await _service.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<AthletePensionAwardPageDto>(result.ErrorCode!, result.ErrorMessage!);
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
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
