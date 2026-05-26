using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2163 / TOR §15.4 INT 004 — schema-driven new-service provisioning REST surface.
/// Provisions a brand-new electronic service in the SI-PS catalogue without code changes
/// or migrations, and retires an existing service-catalog entry. Functional administrator
/// only (<see cref="AuthorizationComposition.CnasAdmin"/>).
/// </summary>
/// <remarks>
/// <para>
/// Route table:
/// <list type="bullet">
///   <item><c>POST /api/admin/service-catalog/provision</c> — create a new passport from a JSON-schema-driven definition.</item>
///   <item><c>POST /api/admin/service-catalog/{code}/retire</c> — soft-deactivate the current revision of a passport.</item>
/// </list>
/// </para>
/// <para>
/// This is a thin controller over <see cref="IServiceCatalogConfigService"/>; all
/// business logic — duplicate-code guard, workflow placeholder seeding, audit emission —
/// lives on the service. The controller's job is shape-validation (via
/// <see cref="NewServiceProvisionInputDto"/> + the auto-registered FluentValidation
/// validator) and HTTP status-code translation.
/// </para>
/// </remarks>
/// <param name="catalog">Underlying schema-driven catalog admin service.</param>
/// <param name="provisionValidator">FluentValidation validator for the provisioning DTO.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/service-catalog")]
public sealed class ServiceCatalogConfigController(
    IServiceCatalogConfigService catalog,
    IValidator<NewServiceProvisionInputDto> provisionValidator) : ControllerBase
{
    private readonly IServiceCatalogConfigService _catalog = catalog;
    private readonly IValidator<NewServiceProvisionInputDto> _provisionValidator = provisionValidator;

    /// <summary>
    /// Provisions a new service-catalog entry from a schema-driven definition. The body
    /// is fully validated by the FluentValidation pipeline before the service is invoked;
    /// the service then runs its own defence-in-depth checks (duplicate-code guard,
    /// workflow placeholder seeding) and emits a Critical <c>SERVICE.PROVISIONED</c>
    /// audit on success.
    /// </summary>
    /// <param name="input">Schema-driven provisioning payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 201 Created with the new <see cref="NewServiceProvisionDto"/>; 400 on validation
    /// failure; 409 when the code is already registered.
    /// </returns>
    [HttpPost("provision")]
    public async Task<ActionResult<NewServiceProvisionDto>> ProvisionAsync(
        [FromBody] NewServiceProvisionInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _provisionValidator
            .ValidateAsync(input, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return ValidationProblem(string.Join("; ", validation.Errors.ConvertAll(e => $"{e.PropertyName}: {e.ErrorMessage}")));
        }

        var result = await _catalog.ProvisionAsync(input, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return StatusCode(StatusCodes.Status201Created, result.Value);
        }
        return MapFailure<NewServiceProvisionDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Retires the current revision of an existing service-catalog entry. Soft-
    /// deactivates the passport (flips <c>IsEnabled=false</c>) and emits a Critical
    /// <c>SERVICE.RETIRED</c> audit. Idempotent — calling on an already-retired passport
    /// returns 200 without an extra audit row.
    /// </summary>
    /// <param name="code">Logical passport code (case-insensitive).</param>
    /// <param name="input">Retirement payload carrying the operator reason.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 on success; 404 when no current row matches the code; 400 when the reason is
    /// empty.
    /// </returns>
    [HttpPost("{code}/retire")]
    public async Task<IActionResult> RetireAsync(
        [FromRoute] string code,
        [FromBody] ServiceRetirementInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var result = await _catalog.RetireAsync(code, input.Reason, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return Ok();
        }
        return MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a <see cref="Result{T}"/> failure to an <see cref="ActionResult{T}"/>.</summary>
    /// <typeparam name="T">The DTO type the action would have returned on success.</typeparam>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 409 / 400 ProblemDetails as appropriate.</returns>
    private ActionResult<T> MapFailure<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Non-generic variant for endpoints that return no body.</summary>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 409 / 400 ProblemDetails as appropriate.</returns>
    private IActionResult MapFailure(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>The numeric HTTP status code.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Conflict => StatusCodes.Status409Conflict,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
