using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.ExternalSources;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0203 / TOR CF 20.06 — admin REST surface over the per-source external
/// ingestion registry. Restricted to the
/// <see cref="AuthorizationComposition.CnasAdmin"/> policy because manual
/// runs touch external data stores.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST /api/admin/external-sources/{sourceCode}/runs?asOfDate=YYYY-MM-DD</c> — trigger manual run.</item>
///   <item><c>GET  /api/admin/external-sources/runs/{sqid}</c> — get a single run.</item>
///   <item><c>GET  /api/admin/external-sources/runs</c> — list runs with filters.</item>
/// </list>
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/external-sources")]
public sealed class ExternalSourceIngestionAdminController : ControllerBase
{
    private readonly IExternalSourceIngestionService _service;

    /// <summary>Constructs the controller.</summary>
    /// <param name="service">External-source ingestion façade.</param>
    public ExternalSourceIngestionAdminController(IExternalSourceIngestionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <summary>
    /// Triggers a manual ingestion run for the supplied source code.
    /// </summary>
    /// <param name="sourceCode">Upper-case source-system code (e.g. <c>RSP</c>).</param>
    /// <param name="asOfDate">Optional ISO yyyy-MM-dd as-of date.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the run DTO; 400 on validation failure.</returns>
    [HttpPost("{sourceCode}/runs")]
    public async Task<ActionResult<ExternalSourceIngestionRunDto>> TriggerManualRunAsync(
        string sourceCode,
        [FromQuery] DateOnly? asOfDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.TriggerManualRunAsync(sourceCode, asOfDate, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ExternalSourceIngestionRunDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Fetches a single ingestion run by its Sqid.
    /// </summary>
    /// <param name="sqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 / 400 / 404.</returns>
    [HttpGet("runs/{sqid}")]
    public async Task<ActionResult<ExternalSourceIngestionRunDto>> GetRunAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetRunByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ExternalSourceIngestionRunDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Lists ingestion runs matching the supplied filter envelope.
    /// </summary>
    /// <param name="sourceCode">Optional source-code filter.</param>
    /// <param name="status">Optional status filter (stable enum-name string).</param>
    /// <param name="triggerKind">Optional trigger-kind filter (stable enum-name string).</param>
    /// <param name="skip">Page offset (default 0).</param>
    /// <param name="take">Page size (default 50; max 100).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the paged DTO envelope.</returns>
    [HttpGet("runs")]
    public async Task<ActionResult<ExternalSourceIngestionRunPageDto>> ListRunsAsync(
        [FromQuery] string? sourceCode = null,
        [FromQuery] string? status = null,
        [FromQuery] string? triggerKind = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new ExternalSourceIngestionRunFilterDto(
            SourceCode: sourceCode,
            Status: status,
            TriggerKind: triggerKind,
            Skip: skip,
            Take: take);
        var result = await _service.ListRunsAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<ExternalSourceIngestionRunPageDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// Maps a failed <see cref="Result{T}"/> to the appropriate HTTP status.
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
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
