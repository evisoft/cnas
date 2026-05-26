using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.MessageBus;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0117 / CF 14.11 / TOR §2.5.5 — admin surface over the Portalul guvernamental de date
/// (PGD) open-data publisher. Distinct from the MCabinet citizen-portal admin surface
/// so per-target rate limits / alerting can be configured independently.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST /api/admin/pgd/publish</c> — publish a dataset payload to PGD.</item>
/// </list>
/// </para>
/// <para>
/// <b>Sqid convention.</b> PGD identifiers (<c>DatasetCode</c>) are admin-chosen stable
/// strings — NOT Sqid-encoded. The same documented exception applies as for
/// <c>WorkflowDefinition.Code</c>: the code IS the public identifier rather than an
/// opaque surrogate.
/// </para>
/// </remarks>
/// <param name="publisher">PGD publisher singleton.</param>
/// <param name="inputValidator">Boundary validator for the publish input.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/pgd")]
public sealed class PgdAdminController(
    IPgdPublisher publisher,
    IValidator<PgdDatasetPublishInputDto> inputValidator) : ControllerBase
{
    private readonly IPgdPublisher _publisher = publisher;
    private readonly IValidator<PgdDatasetPublishInputDto> _inputValidator = inputValidator;

    /// <summary>
    /// Publishes a dataset to PGD. Returns 200 with the outcome on accepted upstream
    /// response or unconfigured publisher (the Outcome's <c>Status</c> = <c>Skipped</c>);
    /// 400 on bad input; 502 on upstream failure.
    /// </summary>
    /// <param name="input">Dataset payload + metadata.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with outcome / 400 on validation failure / 502 on upstream failure.</returns>
    [HttpPost("publish")]
    [Consumes("application/json")]
    public async Task<IActionResult> PublishAsync(
        [FromBody] PgdDatasetPublishInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _inputValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(validation.ToString("; "), statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _publisher.PublishAsync(input, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }
        // Map the two stable PGD failure codes onto distinct HTTP responses so dashboards
        // can chart them separately. Unconfigured = 503 (operator action required);
        // upstream failure = 502 (downstream-dependent).
        return result.ErrorCode switch
        {
            ErrorCodes.PgdNotConfigured => StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new PgdPublishOutcomeDto(PgdPublishStatus.Skipped, null, result.ErrorMessage)),
            ErrorCodes.PgdPublishFailed => StatusCode(
                StatusCodes.Status502BadGateway,
                new PgdPublishOutcomeDto(PgdPublishStatus.Rejected, null, result.ErrorMessage)),
            _ => Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest),
        };
    }
}
