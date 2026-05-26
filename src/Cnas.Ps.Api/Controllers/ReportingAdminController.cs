using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2461 / R2462 / Deliverables 7.1 + 7.2 — admin REST surface for the
/// monthly operational reports (support metrics + error-fix metrics).
/// Restricted to the <see cref="AuthorizationComposition.CnasAdmin"/>
/// policy because the reports surface system-wide operational health
/// figures.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>GET /api/admin/reporting/support-monthly?month=YYYY-MM-01&amp;categoryCodes=A,B</c></item>
///   <item><c>GET /api/admin/reporting/error-fix-monthly?month=YYYY-MM-01</c></item>
/// </list>
/// </para>
/// <para>
/// <b>Month format.</b> The <c>month</c> query parameter is parsed as an
/// invariant-culture <c>YYYY-MM-DD</c> date; the validator enforces
/// <c>Day == 1</c> and "not in the future" so misformed requests return 400.
/// </para>
/// </remarks>
/// <param name="supportService">R2461 monthly support report service.</param>
/// <param name="errorFixService">R2462 monthly error-fix report service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/reporting")]
public sealed class ReportingAdminController(
    IMonthlySupportReportService supportService,
    IMonthlyErrorFixReportService errorFixService) : ControllerBase
{
    private readonly IMonthlySupportReportService _supportService = supportService;
    private readonly IMonthlyErrorFixReportService _errorFixService = errorFixService;

    /// <summary>
    /// Returns the monthly support report (R2461 — Deliverable 7.1).
    /// </summary>
    /// <param name="month">
    /// First-of-month UTC date (<c>YYYY-MM-01</c>). Any other day is
    /// rejected with 400.
    /// </param>
    /// <param name="categoryCodes">
    /// Optional comma-separated list of stable SCREAMING_SNAKE_CASE
    /// category codes (e.g. <c>"AUTH,PAYMENT"</c>) to restrict the
    /// aggregation. Null/empty falls back to all categories.
    /// </param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the report DTO, or 400 on validation failure.</returns>
    [HttpGet("support-monthly")]
    public async Task<ActionResult<MonthlySupportReportDto>> GetSupportMonthlyAsync(
        [FromQuery] string month,
        [FromQuery] string? categoryCodes = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseMonth(month, out var monthValue))
        {
            return Problem(
                "Month must be a YYYY-MM-DD date (e.g. 2026-05-01).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var input = new MonthlySupportReportInputDto(
            Month: monthValue,
            CategoryCodes: ParseCsv(categoryCodes));

        var result = await _supportService.ComputeAsync(input, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Problem(
                result.ErrorMessage,
                statusCode: StatusForCode(result.ErrorCode));
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Returns the monthly error-fix / documentation-update report
    /// (R2462 — Deliverable 7.2).
    /// </summary>
    /// <param name="month">
    /// First-of-month UTC date (<c>YYYY-MM-01</c>). Any other day is
    /// rejected with 400.
    /// </param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the report DTO, or 400 on validation failure.</returns>
    [HttpGet("error-fix-monthly")]
    public async Task<ActionResult<MonthlyErrorFixReportDto>> GetErrorFixMonthlyAsync(
        [FromQuery] string month,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseMonth(month, out var monthValue))
        {
            return Problem(
                "Month must be a YYYY-MM-DD date (e.g. 2026-05-01).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var input = new MonthlyErrorFixReportInputDto(Month: monthValue);
        var result = await _errorFixService.ComputeAsync(input, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Problem(
                result.ErrorMessage,
                statusCode: StatusForCode(result.ErrorCode));
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Parses a <c>YYYY-MM-DD</c> string into a <see cref="DateOnly"/> using
    /// the invariant culture. Returns <c>false</c> for null, whitespace, or
    /// values that do not match the expected format.
    /// </summary>
    /// <param name="raw">Raw query-parameter value.</param>
    /// <param name="value">Parsed value on success.</param>
    /// <returns><c>true</c> when parsing succeeded; <c>false</c> otherwise.</returns>
    private static bool TryParseMonth(string? raw, out DateOnly value)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = default;
            return false;
        }
        return DateOnly.TryParseExact(
            raw,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out value);
    }

    /// <summary>
    /// Splits a comma-separated list (e.g. <c>"AUTH,PAYMENT"</c>) into a
    /// trimmed list of distinct non-empty entries. Returns null when the
    /// caller supplied nothing — the service treats null as "no filter".
    /// </summary>
    /// <param name="csv">Comma-separated raw value.</param>
    /// <returns>Trimmed entries, or null when no values were supplied.</returns>
    private static System.Collections.Generic.List<string>? ParseCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return null;
        }
        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts.ToList();
    }

    /// <summary>
    /// Translates a stable <see cref="ErrorCodes"/> value to an HTTP status.
    /// Unknown / null codes fall back to 400 Bad Request.
    /// </summary>
    /// <param name="code">Stable error code value.</param>
    /// <returns>Mapped HTTP status code.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
