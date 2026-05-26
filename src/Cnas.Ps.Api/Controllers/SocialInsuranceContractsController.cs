using Cnas.Ps.Application.Contributors;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0912 / TOR BP 2.2-C — REST surface for the social-insurance contract
/// lifecycle (issue / modify / terminate) attached to an InsuredPerson.
/// All identifiers crossing the boundary are Sqid-encoded.
/// </summary>
[ApiController]
[Authorize(Roles = "cnas-admin,cnas-user")]
public sealed class SocialInsuranceContractsController : ControllerBase
{
    private readonly ISocialInsuranceContractService _svc;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the controller with its collaborators.</summary>
    /// <param name="svc">Social-insurance contract service façade.</param>
    /// <param name="sqids">Sqid encoder/decoder for route parameters.</param>
    public SocialInsuranceContractsController(
        ISocialInsuranceContractService svc,
        ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(sqids);
        _svc = svc;
        _sqids = sqids;
    }

    /// <summary>R0912 / BP 2.2-C — issue a new social-insurance contract.</summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>201 with the new <see cref="ContributorSocialInsuranceContractDto"/>; 400/404/409 on failure.</returns>
    [HttpPost("api/social-insurance-contracts/issue")]
    public async Task<ActionResult<ContributorSocialInsuranceContractDto>> IssueAsync(
        [FromBody] SocialInsuranceContractIssueDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.IssueAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : MapFailureGeneric<ContributorSocialInsuranceContractDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0912 / BP 2.2-C — supersede an existing current contract.</summary>
    /// <param name="sqid">Sqid-encoded id of the current contract row.</param>
    /// <param name="input">Modify-input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the new (post-supersession) DTO; 400/404/409 on failure.</returns>
    [HttpPut("api/social-insurance-contracts/{sqid}/modify")]
    public async Task<ActionResult<ContributorSocialInsuranceContractDto>> ModifyAsync(
        string sqid,
        [FromBody] SocialInsuranceContractModifyDto input,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<ContributorSocialInsuranceContractDto>(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var result = await _svc.ModifyAsync(decoded.Value, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<ContributorSocialInsuranceContractDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0912 / BP 2.2-C — terminate an active contract.</summary>
    /// <param name="sqid">Sqid-encoded id of the active contract row.</param>
    /// <param name="input">Terminate-input envelope (effective date + reason).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>204 on success; 400/404/409 on failure.</returns>
    [HttpPost("api/social-insurance-contracts/{sqid}/terminate")]
    public async Task<IActionResult> TerminateAsync(
        string sqid,
        [FromBody] SocialInsuranceContractTerminateDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureBare(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var result = await _svc.TerminateAsync(decoded.Value, input.EffectiveDate, input.Reason, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0912 / BP 2.2-C — fetch the current contract(s) for a Contributor.</summary>
    /// <param name="contributorSqid">Sqid-encoded Contributor (InsuredPerson) id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the (possibly empty) list.</returns>
    [HttpGet("api/contributors/{contributorSqid}/social-insurance-contracts/current")]
    public async Task<ActionResult<IReadOnlyList<ContributorSocialInsuranceContractDto>>> GetCurrentAsync(
        string contributorSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(contributorSqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<IReadOnlyList<ContributorSocialInsuranceContractDto>>(
                decoded.ErrorCode, decoded.ErrorMessage);
        }
        var rows = await _svc.GetCurrentForContributorAsync(decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return Ok(rows);
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
