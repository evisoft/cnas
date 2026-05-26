using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Captcha;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0507 / TOR CF 01.10 — anonymous-accessible REST surface that mints and
/// verifies self-issued CAPTCHA challenges. Used by the public-catalog search
/// gate (<see cref="PublicCatalogController"/>) when the
/// <see cref="ICaptchaPolicyEvaluator"/> classifies an inbound query as
/// "broad". Rate-limited by the <see cref="RateLimitingPolicies.Anonymous"/>
/// policy so a bot cannot drain the challenge mint by hot-looping the issue
/// endpoint.
/// </summary>
/// <param name="service">Self-issued CAPTCHA challenge service.</param>
/// <param name="verifyValidator">FluentValidation validator for the verify-input DTO.</param>
[ApiController]
[EnableRateLimiting(RateLimitingPolicies.Anonymous)]
[Route("api/captcha")]
public sealed class CaptchaController(
    ICaptchaChallengeService service,
    IValidator<CaptchaVerifyInputDto> verifyValidator) : ControllerBase
{
    private readonly ICaptchaChallengeService _service = service;
    private readonly IValidator<CaptchaVerifyInputDto> _verifyValidator = verifyValidator;

    /// <summary>
    /// Issues a fresh CAPTCHA challenge: opaque token + image bytes the
    /// client renders so the user can read the code.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with <see cref="CaptchaIssueDto"/>.</returns>
    [HttpGet("challenge")]
    public async Task<ActionResult<CaptchaIssueDto>> ChallengeAsync(CancellationToken cancellationToken = default)
    {
        var dto = await _service.IssueAsync(cancellationToken).ConfigureAwait(false);
        return Ok(dto);
    }

    /// <summary>
    /// Verifies a user answer against a previously-issued challenge. On
    /// success the underlying token is marked verified — subsequent calls to
    /// the gated public surface within the post-verify window accept the
    /// token via the <c>X-Captcha-Token</c> header.
    /// </summary>
    /// <param name="input">Token + answer envelope.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 on success; 400 ProblemDetails with stable
    /// <c>errorCode</c> extension on failure.
    /// </returns>
    [HttpPost("verify")]
    [Consumes("application/json")]
    public async Task<IActionResult> VerifyAsync(
        [FromBody] CaptchaVerifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);

        // FluentValidation gate — reject empty / oversized challenge tokens and answers
        // before the service performs the (more expensive) HMAC verification. Failures
        // collapse to a 400 ProblemDetails with the stable VALIDATION_FAILED code so
        // the client treats them identically to a bad-input rejection.
        var validation = await _verifyValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            var failProblem = new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "CAPTCHA verification failed.",
                Detail = string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
            };
            failProblem.Extensions["errorCode"] = ErrorCodes.ValidationFailed;
            return new ObjectResult(failProblem)
            {
                StatusCode = StatusCodes.Status400BadRequest,
                ContentTypes = { "application/problem+json" },
            };
        }

        var result = await _service
            .VerifyAsync(input.ChallengeToken, input.Answer, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return Ok();
        }
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "CAPTCHA verification failed.",
            Detail = result.ErrorMessage,
        };
        problem.Extensions["errorCode"] = result.ErrorCode;
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" },
        };
    }
}
