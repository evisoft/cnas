using Cnas.Ps.Application.Financials;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0816 / TOR BP 1.2-G — REST surface for the Treasury-information export.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation.</b> Restricted to the <c>cnas-admin</c> role — the
/// export reveals refund + outstanding-claim financial details.
/// </para>
/// <para>
/// <b>Content negotiation.</b> The caller picks XML or CSV via the
/// <c>format</c> query parameter (default <c>xml</c>). The response carries
/// <c>application/xml</c> or <c>text/csv</c> respectively with a
/// <c>Content-Disposition: attachment</c> header so browsers download the
/// file directly.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "cnas-admin")]
public sealed class TreasuryInformationController : ControllerBase
{
    private readonly ITreasuryInformationExporter _exporter;

    /// <summary>Constructs the controller with its collaborators.</summary>
    /// <param name="exporter">Treasury-information exporter façade.</param>
    public TreasuryInformationController(ITreasuryInformationExporter exporter)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        _exporter = exporter;
    }

    /// <summary>R0816 — generate a Treasury-information export.</summary>
    /// <param name="forDate">Operating date the export targets (must be ≤ today).</param>
    /// <param name="format">Output format — <c>xml</c> or <c>csv</c> (default <c>xml</c>).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// 200 with the file payload on success; 400 on validation failure
    /// (future date, unsupported format).
    /// </returns>
    [HttpGet("api/treasury/information")]
    public async Task<IActionResult> GenerateAsync(
        [FromQuery] DateOnly forDate,
        [FromQuery] string? format = "xml",
        CancellationToken cancellationToken = default)
    {
        var result = await _exporter.GenerateAsync(forDate, format ?? "xml", cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Problem(result.ErrorMessage, statusCode: StatusForCode(result.ErrorCode));
        }

        var dto = result.Value!;
        var contentType = string.Equals(dto.Format, "XML", StringComparison.OrdinalIgnoreCase)
            ? "application/xml"
            : "text/csv";
        return File(dto.Content, contentType, dto.FileName);
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
