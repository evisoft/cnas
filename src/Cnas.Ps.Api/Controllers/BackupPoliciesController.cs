using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Backups;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2307 / TOR SEC 060 — admin REST surface over the backup-policy registry.
/// Restricted to <see cref="AuthorizationComposition.CnasAdmin"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST   /api/admin/backups/policies</c> — create.</item>
///   <item><c>PUT    /api/admin/backups/policies/{sqid}</c> — modify (non-Archived).</item>
///   <item><c>POST   /api/admin/backups/policies/{sqid}/activate</c> — activate.</item>
///   <item><c>POST   /api/admin/backups/policies/{sqid}/deactivate</c> — deactivate.</item>
///   <item><c>POST   /api/admin/backups/policies/{sqid}/archive</c> — archive (soft-delete).</item>
///   <item><c>GET    /api/admin/backups/policies/{sqid}</c> — get by Sqid.</item>
///   <item><c>GET    /api/admin/backups/policies/by-code/{code}</c> — get by stable code.</item>
///   <item><c>GET    /api/admin/backups/policies?isActive=…&amp;scope=…&amp;skip=…&amp;take=…</c> — list.</item>
/// </list>
/// </para>
/// </remarks>
/// <param name="service">Backup-policy service façade.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/backups/policies")]
public sealed class BackupPoliciesController(IBackupPolicyService service) : ControllerBase
{
    private readonly IBackupPolicyService _service = service;

    /// <summary>Creates a new backup policy.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the created policy; 400 on validation failure; 409 on duplicate.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<BackupPolicyDto>> CreateAsync(
        [FromBody] BackupPolicyCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BackupPolicyDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Modifies an existing non-Archived policy.</summary>
    /// <param name="sqid">Sqid-encoded policy id.</param>
    /// <param name="input">Modify payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated policy; 400 / 404 / 409 on failure.</returns>
    [HttpPut("{sqid}")]
    [Consumes("application/json")]
    public async Task<ActionResult<BackupPolicyDto>> ModifyAsync(
        string sqid,
        [FromBody] BackupPolicyModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ModifyAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BackupPolicyDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Activates an Inactive policy.</summary>
    /// <param name="sqid">Sqid-encoded policy id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated policy.</returns>
    [HttpPost("{sqid}/activate")]
    public async Task<ActionResult<BackupPolicyDto>> ActivateAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ActivateAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BackupPolicyDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Deactivates an Active policy.</summary>
    /// <param name="sqid">Sqid-encoded policy id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated policy.</returns>
    [HttpPost("{sqid}/deactivate")]
    public async Task<ActionResult<BackupPolicyDto>> DeactivateAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.DeactivateAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BackupPolicyDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Archives a policy (soft-delete).</summary>
    /// <param name="sqid">Sqid-encoded policy id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated policy.</returns>
    [HttpPost("{sqid}/archive")]
    [Consumes("application/json")]
    public async Task<ActionResult<BackupPolicyDto>> ArchiveAsync(
        string sqid,
        [FromBody] BackupPolicyReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _service.ArchiveAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BackupPolicyDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets a policy by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded policy id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the policy; 400 / 404 on failure.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<BackupPolicyDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BackupPolicyDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets a policy by its stable code.</summary>
    /// <param name="code">Policy code (SCREAMING_SNAKE_CASE).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the policy; 400 / 404 on failure.</returns>
    [HttpGet("by-code/{code}")]
    public async Task<ActionResult<BackupPolicyDto>> GetByCodeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetByCodeAsync(code, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BackupPolicyDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists policies matching the filter.</summary>
    /// <param name="isActive">Optional IsActive filter.</param>
    /// <param name="scope">Optional scope filter (stable enum-name).</param>
    /// <param name="skip">Page offset (≥ 0).</param>
    /// <param name="take">Page size (1..100).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the page; 400 on validation failure.</returns>
    [HttpGet]
    public async Task<ActionResult<BackupPolicyPageDto>> ListAsync(
        [FromQuery] bool? isActive = null,
        [FromQuery] string? scope = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new BackupPolicyFilterDto(isActive, scope, skip, take);
        var result = await _service.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BackupPolicyPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Translates a failed <see cref="Result{T}"/> into the appropriate
    /// <see cref="ActionResult"/>: invalid-sqid / validation → 400,
    /// not-found → 404, conflict → 409, anything else → 500.
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
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            IBackupPolicyService.DuplicatePolicyCodeCode => Conflict(new { error = errorCode, message = errorMessage }),
            IBackupPolicyService.InvalidTransitionCode => Conflict(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
