using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Backups;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2307 / TOR SEC 060 — admin REST surface over the runtime backup
/// orchestrator: manually trigger a run, list runs, fetch a run, recheck
/// integrity, and force a retention sweep. Restricted to
/// <see cref="AuthorizationComposition.CnasAdmin"/>.
/// </summary>
/// <param name="orchestrator">Backup orchestrator façade.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/backups")]
public sealed class BackupAdminController(IBackupOrchestrator orchestrator) : ControllerBase
{
    private readonly IBackupOrchestrator _orchestrator = orchestrator;

    /// <summary>Triggers a manual backup run for the supplied policy.</summary>
    /// <param name="policySqid">Sqid-encoded policy id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the persisted run DTO; 400 / 404 / 409 on failure.</returns>
    [HttpPost("policies/{policySqid}/runs")]
    public async Task<ActionResult<BackupRunDto>> TriggerManualRunAsync(
        string policySqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _orchestrator
            .RunPolicyAsync(policySqid, BackupTriggerKind.Manual, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BackupRunDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets a backup run by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the run DTO; 400 / 404 on failure.</returns>
    [HttpGet("runs/{sqid}")]
    public async Task<ActionResult<BackupRunDto>> GetRunAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _orchestrator.GetRunByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BackupRunDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists backup runs matching the filter.</summary>
    /// <param name="policySqid">Optional Sqid-encoded policy filter.</param>
    /// <param name="status">Optional status filter (stable enum-name).</param>
    /// <param name="triggerKind">Optional trigger-kind filter (stable enum-name).</param>
    /// <param name="skip">Page offset (≥ 0).</param>
    /// <param name="take">Page size (1..100).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the page; 400 on validation failure.</returns>
    [HttpGet("runs")]
    public async Task<ActionResult<BackupRunPageDto>> ListRunsAsync(
        [FromQuery] string? policySqid = null,
        [FromQuery] string? status = null,
        [FromQuery] string? triggerKind = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new BackupRunFilterDto(policySqid, status, triggerKind, StartedAfter: null, skip, take);
        var result = await _orchestrator.ListRunsAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BackupRunPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Re-verifies the on-target integrity of a backup run.</summary>
    /// <param name="sqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the integrity-check DTO; 400 / 404 / 409 on failure.</returns>
    [HttpPost("runs/{sqid}/retry-integrity-check")]
    public async Task<ActionResult<BackupIntegrityCheckDto>> RetryIntegrityCheckAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _orchestrator.RetryIntegrityCheckAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<BackupIntegrityCheckDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Manually triggers the retention sweep.</summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the purged-count envelope.</returns>
    [HttpPost("sweep-expired")]
    public async Task<ActionResult<BackupSweepResponse>> SweepExpiredAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _orchestrator.SweepExpiredRunsAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new BackupSweepResponse(result.Value))
            : MapFailure<BackupSweepResponse>(result.ErrorCode!, result.ErrorMessage!);
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
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.Conflict => Conflict(new { error = errorCode, message = errorMessage }),
            IBackupOrchestrator.PolicyNotActiveCode => Conflict(new { error = errorCode, message = errorMessage }),
            IBackupOrchestrator.ProviderNotConfiguredCode => StatusCode(500, new { error = errorCode, message = errorMessage }),
            IBackupTarget.TargetNotConfiguredCode => StatusCode(500, new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}

/// <summary>R2307 / TOR SEC 060 — response envelope for the retention-sweep endpoint.</summary>
/// <param name="PurgedCount">Number of run payloads removed by the sweep.</param>
public sealed record BackupSweepResponse(int PurgedCount);
