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
/// R0116 + R0195 / TOR SEC 054-055 — admin REST surface for the MLog
/// dual-write category filter registry.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/mlog/categories")]
public sealed class MLogCategoriesAdminController : ControllerBase
{
    private readonly IMLogCategoryConfigService _svc;
    private readonly IValidator<MLogCategoryConfigInputDto> _validator;

    /// <summary>Constructs the controller.</summary>
    /// <param name="svc">Category-config service.</param>
    /// <param name="validator">FluentValidation validator.</param>
    public MLogCategoriesAdminController(
        IMLogCategoryConfigService svc,
        IValidator<MLogCategoryConfigInputDto> validator)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(validator);
        _svc = svc;
        _validator = validator;
    }

    /// <summary>R0116 + R0195 — list category configs.</summary>
    /// <param name="includeInactive">Include deactivated rows when <c>true</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MLogCategoryConfigDto>>> ListAsync(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ListAsync(includeInactive, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : Problem(result.ErrorMessage);
    }

    /// <summary>R0116 + R0195 — upsert a category config row.</summary>
    /// <param name="input">Config payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the persisted DTO; 400 on invalid input.</returns>
    [HttpPost]
    public async Task<ActionResult<MLogCategoryConfigDto>> UpsertAsync(
        [FromBody] MLogCategoryConfigInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var validation = await _validator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }
        var result = await _svc.UpsertAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : Problem(result.ErrorMessage);
    }

    /// <summary>R0116 + R0195 — deactivate a category config row.</summary>
    /// <param name="sqid">Sqid-encoded config id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 on success, 404 if unknown.</returns>
    [HttpDelete("{sqid}")]
    public async Task<IActionResult> DeactivateAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.DeactivateAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return NoContent();
        }
        return result.ErrorCode == ErrorCodes.NotFound
            ? NotFound()
            : Problem(result.ErrorMessage);
    }
}
