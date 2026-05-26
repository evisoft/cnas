using System.Globalization;
using Cnas.Ps.Application.Identity;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2274 / TOR SEC 028 — REST surface for the "who can do what" access-rights
/// report. Exposes JSON projections for by-user / by-role / by-group queries
/// and two CSV exports (single role + full matrix).
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation.</b> Every endpoint is gated by the
/// <c>cnas-admin</c> role — these endpoints aggregate identity-graph data
/// and must not be reachable by ordinary CNAS staff or citizens. The
/// follow-up phase R2273 (4-eyes admin) will refine the policy to a
/// dedicated <c>IdentityAdmin</c> name.
/// </para>
/// <para>
/// <b>Confidential response.</b> The JSON and CSV bodies aggregate user
/// identifiers + emails + role grants — that is Confidential / PII content
/// in CLAUDE.md SEC 044 terms. Every successful call writes an
/// <c>ACCESS_RIGHTS_REPORT.GENERATED</c> audit row at
/// <c>AuditSeverity.Information</c> so disclosures are end-to-end
/// traceable; the audit payload deliberately records only the report kind +
/// row count (no PII).
/// </para>
/// <para>
/// <b>Sqid round-trip.</b> Route parameters are decoded inside the service
/// layer via <see cref="ISqidService.TryDecode"/>; outbound DTOs carry
/// Sqid-encoded ids per CLAUDE.md RULE 3. Role and group codes stay as
/// plain text — they are stable domain identifiers, not Sqids.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "cnas-admin")]
public sealed class AccessRightsReportsController : ControllerBase
{
    /// <summary>Stable content-type for CSV exports.</summary>
    private const string CsvContentType = "text/csv";

    private readonly IAccessRightsReportService _svc;
    private readonly ICnasTimeProvider _clock;

    /// <summary>Constructs the controller with its collaborators.</summary>
    /// <param name="svc">Report-aggregation service.</param>
    /// <param name="clock">UTC clock — used to date the CSV filename.</param>
    public AccessRightsReportsController(IAccessRightsReportService svc, ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(clock);
        _svc = svc;
        _clock = clock;
    }

    /// <summary>R2274 — returns a single user's effective access picture.</summary>
    /// <param name="userSqid">Sqid-encoded user-profile id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the DTO; 400 on bad Sqid; 404 when the user is missing.</returns>
    [HttpGet("api/access-rights-reports/by-user/{userSqid}")]
    public async Task<ActionResult<AccessRightsByUserReportDto>> GetByUserAsync(
        string userSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ReportByUserAsync(userSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<AccessRightsByUserReportDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R2274 — returns the paged set of users holding the supplied role.</summary>
    /// <param name="roleCode">Stable role code.</param>
    /// <param name="skip">Pagination skip count (≥ 0).</param>
    /// <param name="take">Pagination page size (1..500).</param>
    /// <param name="includeDisabledAccounts">When true, suspended/disabled/locked accounts are included.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the DTO; 400 on bad input.</returns>
    [HttpGet("api/access-rights-reports/by-role/{roleCode}")]
    public async Task<ActionResult<AccessRightsByRoleReportDto>> GetByRoleAsync(
        string roleCode,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        [FromQuery] bool includeDisabledAccounts = false,
        CancellationToken cancellationToken = default)
    {
        var paging = new AccessRightsReportPagingDto(skip, take, includeDisabledAccounts);
        var result = await _svc.ReportByRoleAsync(roleCode, paging, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<AccessRightsByRoleReportDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R2274 — returns a group's effective access picture.</summary>
    /// <param name="groupSqid">Sqid-encoded group id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the DTO; 400 on bad Sqid; 404 when the group is missing.</returns>
    [HttpGet("api/access-rights-reports/by-group/{groupSqid}")]
    public async Task<ActionResult<AccessRightsByGroupReportDto>> GetByGroupAsync(
        string groupSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ReportByGroupAsync(groupSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<AccessRightsByGroupReportDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R2274 — streams a UTF-8 CSV of every user effectively holding the supplied role.</summary>
    /// <param name="roleCode">Stable role code.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with text/csv body; 400 on bad input.</returns>
    [HttpGet("api/access-rights-reports/by-role/{roleCode}/export.csv")]
    public async Task<ActionResult> ExportByRoleCsvAsync(
        string roleCode,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ExportByRoleCsvAsync(roleCode, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            var status = StatusForCode(result.ErrorCode);
            return status == StatusCodes.Status404NotFound
                ? NotFound()
                : Problem(result.ErrorMessage, statusCode: status);
        }

        var date = _clock.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return File(
            result.Value,
            CsvContentType,
            $"access-rights-by-role-{roleCode}-{date}.csv");
    }

    /// <summary>R2274 — streams a UTF-8 CSV of every (user, role) tuple in the access matrix.</summary>
    /// <param name="skip">Pagination skip count (≥ 0).</param>
    /// <param name="take">Pagination page size (1..500).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with text/csv body; 400 on bad input.</returns>
    [HttpGet("api/access-rights-reports/full-matrix.csv")]
    public async Task<ActionResult> ExportFullMatrixCsvAsync(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 500,
        CancellationToken cancellationToken = default)
    {
        var paging = new AccessRightsReportPagingDto(skip, take, IncludeDisabledAccounts: false);
        var result = await _svc.ExportFullAccessMatrixCsvAsync(paging, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            var status = StatusForCode(result.ErrorCode);
            return status == StatusCodes.Status404NotFound
                ? NotFound()
                : Problem(result.ErrorMessage, statusCode: status);
        }

        var date = _clock.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return File(
            result.Value,
            CsvContentType,
            $"access-rights-full-matrix-{date}.csv");
    }

    /// <summary>Maps generic-result failures to ProblemDetails.</summary>
    /// <typeparam name="T">DTO type the action would have returned.</typeparam>
    /// <param name="code">Stable error code from the service.</param>
    /// <param name="message">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ActionResult<T> MapFailure<T>(string? code, string? message)
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
