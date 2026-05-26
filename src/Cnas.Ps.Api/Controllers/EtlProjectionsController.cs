using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Etl;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0153 / TOR CF 19.05 — admin surface over the contributor period-aware
/// projection pipeline. The two run endpoints (per-contributor + batch) are
/// gated by <see cref="AuthorizationComposition.CnasTechAdmin"/> — a manual
/// recompute is a maintenance action, not a routine one. The read endpoint
/// is gated by <see cref="AuthorizationComposition.CnasUser"/> — every
/// authenticated CNAS operator can resolve "as-of date" queries against the
/// projection store.
/// </summary>
/// <remarks>
/// <para>
/// Route table:
/// <list type="bullet">
///   <item><c>POST /api/etl/contributor-projections/run/{contributorSqid}</c> — admin force-rebuild for one contributor.</item>
///   <item><c>POST /api/etl/contributor-projections/run-all</c> — admin force-rebuild for everyone.</item>
///   <item><c>GET  /api/etl/contributor-projections/{contributorSqid}?asOfUtc=YYYY-MM-DDTHH:mm:ssZ</c> — period query.</item>
/// </list>
/// </para>
/// <para>
/// <b>Policy mapping.</b> The TOR's <c>Etl.Manage</c> and <c>Etl.View</c>
/// permission names are realised through the existing
/// <see cref="AuthorizationComposition.CnasTechAdmin"/> (manage) and
/// <see cref="AuthorizationComposition.CnasUser"/> (view) policies — see
/// <c>AuthorizationComposition</c> for the full RBAC mapping.
/// </para>
/// </remarks>
/// <param name="svc">Underlying projection orchestrator + read facade.</param>
/// <param name="sqids">Sqid encoder/decoder for the route parameters.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasUser)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/etl/contributor-projections")]
public sealed class EtlProjectionsController(
    IContributorPeriodProjectionService svc,
    ISqidService sqids) : ControllerBase
{
    private readonly IContributorPeriodProjectionService _svc = svc;
    private readonly ISqidService _sqids = sqids;

    /// <summary>
    /// Admin-only forced rebuild of the projection rows for a single
    /// contributor. Idempotent — the service performs DELETE-then-INSERT
    /// per contributor, so a re-run produces the same slice set as long as
    /// the underlying source rows are unchanged.
    /// </summary>
    /// <param name="contributorSqid">Sqid-encoded id of the InsuredPerson.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with the <see cref="ContributorPeriodProjectionRunDto"/> summary on
    /// success; ProblemDetails on failure.
    /// </returns>
    [HttpPost("run/{contributorSqid}")]
    [Authorize(Policy = AuthorizationComposition.CnasTechAdmin)]
    public async Task<ActionResult<ContributorPeriodProjectionRunDto>> RunForContributorAsync(
        [FromRoute] string contributorSqid,
        CancellationToken cancellationToken = default)
    {
        if (!TryDecodeSqid(contributorSqid, out var contributorId))
        {
            return Problem(
                "Invalid contributor identifier.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _svc.RebuildForContributorAsync(contributorId, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Problem(result.ErrorMessage, statusCode: StatusForCode(result.ErrorCode));
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Admin-only forced rebuild of the projection rows for every
    /// contributor. Idempotent — DELETE-then-INSERT per contributor.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with the <see cref="ContributorPeriodProjectionRunDto"/> batch
    /// summary on success; ProblemDetails on failure.
    /// </returns>
    [HttpPost("run-all")]
    [Authorize(Policy = AuthorizationComposition.CnasTechAdmin)]
    public async Task<ActionResult<ContributorPeriodProjectionRunDto>> RunAllAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.RebuildAllAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Problem(result.ErrorMessage, statusCode: StatusForCode(result.ErrorCode));
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Period-aware query — returns every projection row whose half-open
    /// <c>[PeriodStartUtc, PeriodEndUtc)</c> interval covers
    /// <paramref name="asOfUtc"/>. Most lookups will hit exactly one row; the
    /// API returns a list so boundary edge cases are surfaced.
    /// </summary>
    /// <param name="contributorSqid">Sqid-encoded id of the InsuredPerson.</param>
    /// <param name="asOfUtc">UTC instant being asked about.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with the matching DTOs; 400 ProblemDetails when the sqid is
    /// malformed.
    /// </returns>
    [HttpGet("{contributorSqid}")]
    public async Task<ActionResult<IReadOnlyList<ContributorPeriodProjectionDto>>> QueryAsync(
        [FromRoute] string contributorSqid,
        [FromQuery] DateTime asOfUtc,
        CancellationToken cancellationToken = default)
    {
        if (!TryDecodeSqid(contributorSqid, out var contributorId))
        {
            return Problem(
                "Invalid contributor identifier.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var rows = await _svc.QueryAsync(contributorId, asOfUtc, cancellationToken)
            .ConfigureAwait(false);
        return Ok(rows);
    }

    /// <summary>
    /// Defensively decodes a Sqid-encoded route parameter. Returns
    /// <c>false</c> when the string is null/empty or the encoder rejects it.
    /// </summary>
    /// <param name="sqid">Inbound Sqid string from the route.</param>
    /// <param name="id">Decoded internal id; <c>0</c> on failure.</param>
    /// <returns><c>true</c> when decoding succeeded; <c>false</c> otherwise.</returns>
    private bool TryDecodeSqid(string sqid, out long id)
    {
        id = 0;
        var result = _sqids.TryDecode(sqid);
        if (result.IsFailure)
        {
            return false;
        }
        id = result.Value;
        return id > 0;
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>The mapped HTTP status code.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
