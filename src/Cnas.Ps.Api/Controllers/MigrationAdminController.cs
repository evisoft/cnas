using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Migration;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2430 / R2431 / R2433 / TOR M4 — admin REST surface over the
/// migration runs / findings / reconciliation. Restricted to the
/// <see cref="AuthorizationComposition.CnasAdmin"/> policy.
/// </summary>
/// <param name="adminService">Migration admin service façade.</param>
/// <param name="reconciler">Reconciler façade for explicit recompute.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/migration")]
public sealed class MigrationAdminController(
    IMigrationAdminService adminService,
    IMigrationReconciler reconciler) : ControllerBase
{
    private readonly IMigrationAdminService _admin = adminService;
    private readonly IMigrationReconciler _reconciler = reconciler;

    /// <summary>Triggers a manual import (DryRun or Apply) for a plan.</summary>
    /// <param name="planSqid">Sqid-encoded plan id.</param>
    /// <param name="dryRun">When true triggers a DryRun (default true).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the run summary; 400 / 404 / 409 on failure.</returns>
    [HttpPost("plans/{planSqid}/runs")]
    public async Task<ActionResult<MigrationRunSummaryDto>> TriggerManualImportAsync(
        string planSqid,
        [FromQuery] bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        var result = await _admin.TriggerManualImportAsync(planSqid, dryRun, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MigrationRunSummaryDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets a run summary by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the run DTO.</returns>
    [HttpGet("runs/{sqid}")]
    public async Task<ActionResult<MigrationRunDto>> GetRunAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _admin.GetRunByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MigrationRunDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets a run + paged findings + all batches.</summary>
    /// <param name="sqid">Sqid-encoded run id.</param>
    /// <param name="skip">Findings page offset.</param>
    /// <param name="take">Findings page size.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the details DTO.</returns>
    [HttpGet("runs/{sqid}/details")]
    public async Task<ActionResult<MigrationRunDetailsDto>> GetRunDetailsAsync(
        string sqid,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new MigrationRunDetailsFilterDto(skip, take);
        var result = await _admin.GetRunDetailsAsync(sqid, filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MigrationRunDetailsDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists runs.</summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="triggerKind">Optional trigger-kind filter.</param>
    /// <param name="planSqid">Optional plan Sqid filter.</param>
    /// <param name="skip">Page offset.</param>
    /// <param name="take">Page size.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the page.</returns>
    [HttpGet("runs")]
    public async Task<ActionResult<MigrationRunPageDto>> ListRunsAsync(
        [FromQuery] string? status = null,
        [FromQuery] string? triggerKind = null,
        [FromQuery] string? planSqid = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new MigrationRunFilterDto(status, triggerKind, planSqid, skip, take);
        var result = await _admin.ListRunsAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MigrationRunPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Computes (or recomputes) the reconciliation report for a run.</summary>
    /// <param name="sqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the reconciliation DTO.</returns>
    [HttpPost("runs/{sqid}/reconcile")]
    public async Task<ActionResult<ReconciliationReportDto>> ReconcileAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _reconciler.ReconcileAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ReconciliationReportDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Gets the cached reconciliation report for a run.</summary>
    /// <param name="sqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the reconciliation DTO.</returns>
    [HttpGet("runs/{sqid}/reconciliation")]
    public async Task<ActionResult<ReconciliationReportDto>> GetReconciliationAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _admin.GetReconciliationAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ReconciliationReportDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Lists findings.</summary>
    /// <param name="severity">Optional severity filter.</param>
    /// <param name="runSqid">Optional run-id Sqid filter.</param>
    /// <param name="findingCode">Optional finding-code filter.</param>
    /// <param name="acknowledged">Optional acknowledgement-state filter.</param>
    /// <param name="skip">Page offset.</param>
    /// <param name="take">Page size.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the page.</returns>
    [HttpGet("findings")]
    public async Task<ActionResult<MigrationFindingPageDto>> ListFindingsAsync(
        [FromQuery] string? severity = null,
        [FromQuery] string? runSqid = null,
        [FromQuery] string? findingCode = null,
        [FromQuery] bool? acknowledged = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new MigrationFindingFilterDto(severity, runSqid, findingCode, acknowledged, skip, take);
        var result = await _admin.ListFindingsAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MigrationFindingPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Acknowledges a finding.</summary>
    /// <param name="sqid">Sqid-encoded finding id.</param>
    /// <param name="input">Acknowledgement payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated finding DTO.</returns>
    [HttpPost("findings/{sqid}/acknowledge")]
    [Consumes("application/json")]
    public async Task<ActionResult<MigrationFindingDto>> AcknowledgeFindingAsync(
        string sqid,
        [FromBody] MigrationFindingAcknowledgeInputDto input,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var result = await _admin.AcknowledgeFindingAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<MigrationFindingDto>(result.ErrorCode!, result.ErrorMessage!);
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
            IMigrationImporter.PeakHourGateBlockedCode => Conflict(new { error = errorCode, message = errorMessage }),
            IMigrationImporter.PlanNotActiveCode => Conflict(new { error = errorCode, message = errorMessage }),
            IMigrationImporter.PlanNotFoundCode => NotFound(new { error = errorCode, message = errorMessage }),
            IMigrationImporter.SourceNotConfiguredCode => StatusCode(500, new { error = errorCode, message = errorMessage }),
            IMigrationReconciler.RunNotFoundCode => NotFound(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
