using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// UC09 / UC19 — Reports REST surface. Two endpoints:
/// <list type="bullet">
///   <item><c>GET /api/reports</c> — open to any authenticated CNAS staff role; returns the
///         report catalogue.</item>
///   <item><c>POST /api/reports/{code}/generate</c> — restricted to the
///         <see cref="AuthorizationComposition.CnasDecider"/> policy (deciders + admins);
///         streams the generated report bytes back to the caller as a downloadable file.</item>
/// </list>
/// Both routes are user-partitioned via the <see cref="RateLimitingPolicies.Authenticated"/>
/// limiter and require a valid bearer token (the <c>[Authorize]</c> on the catalogue route
/// inherits the staff-role policy through <c>CnasUser</c>; the generate route narrows further
/// to <c>CnasDecider</c>).
/// </summary>
/// <param name="reports">Underlying reporting service.</param>
/// <param name="exportSelector">
/// R0529 / TOR CF 03.14 — universal report-export pipeline. Used by the
/// <c>GET /api/reports/{code}/export</c> action to dispatch a
/// <see cref="ReportExportInputDto"/> to the matching
/// <see cref="IReportExporter"/> (Csv / Xlsx / Docx / Pdf).
/// </param>
/// <param name="clock">UTC clock for stamping the suggested filename.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasUser)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/reports")]
public sealed class ReportsController(
    IReportingService reports,
    IReportExportSelector exportSelector,
    ICnasTimeProvider clock) : ControllerBase
{
    private readonly IReportingService _reports = reports;
    private readonly IReportExportSelector _exportSelector = exportSelector;
    private readonly ICnasTimeProvider _clock = clock;

    /// <summary>
    /// R0529 — fallback header used when the underlying CSV stream is empty
    /// (no rows AND no header). Lifted to a static readonly field per
    /// CA1861 so the array allocation does not repeat on every export.
    /// </summary>
    private static readonly string[] FallbackHeaders = ["Value"];


    /// <summary>
    /// Lists every report code recognised by the reporting service together with its display
    /// title in each of the three supported UI languages. Used by the front-end to populate
    /// the report-picker drop-down without hard-coding the catalogue.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the catalogue; 400 ProblemDetails on unexpected service failure.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ReportCatalogEntryOutput>>> ListAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _reports.ListAvailableAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<IReadOnlyList<ReportCatalogEntryOutput>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Renders a report identified by its stable <paramref name="code"/> in the requested
    /// format and streams the bytes back inline as a downloadable file. Restricted to the
    /// <see cref="AuthorizationComposition.CnasDecider"/> policy because reports may aggregate
    /// PII (audit logs, beneficiary registries) that lower-privileged roles should not export.
    /// </summary>
    /// <param name="code">Stable report code (e.g. <c>AUDIT_LOG</c>, <c>RPT-PEN-ACTIVE</c>).</param>
    /// <param name="body">Format + per-report parameter dictionary.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with a <see cref="FileStreamResult"/> whose <c>ContentType</c>
    /// mirrors the requested format (CSV / XLSX / PDF) and whose
    /// <c>FileDownloadName</c> embeds the report code and extension.
    /// 404 when the code is unknown; 400 on validation failure; 413 (mapped to 400 +
    /// <see cref="ErrorCodes.ReportTooLarge"/> via ProblemDetails) when the row ceiling is
    /// exceeded.
    /// </returns>
    [HttpPost("{code}/generate")]
    [Authorize(Policy = AuthorizationComposition.CnasDecider)]
    public async Task<IActionResult> GenerateAsync(
        [FromRoute] string code,
        [FromBody] ReportGenerateRequest body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        // Serialise the per-report parameter dictionary to JSON before forwarding to the
        // service: the underlying GenerateAsync surface takes a JSON document because the
        // dispatcher uses System.Text.Json's lenient readers (ReadUtcDate / ReadInt /
        // ReadString) for typed extraction. Doing the conversion at the boundary lets the
        // service stay JSON-native without leaking its choice into the API contract.
        var parametersJson = SerialiseParameters(body.Parameters);

        var result = await _reports.GenerateAsync(code, parametersJson, body.Format, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return MapFailureBare(result.ErrorCode, result.ErrorMessage);
        }

        var stream = result.Value;
        var contentType = ContentTypeFor(body.Format);
        var fileName = $"{code}{ExtensionFor(body.Format)}";
        return File(stream, contentType, fileName);
    }

    /// <summary>
    /// R0529 / TOR CF 03.14 — extended export surface that adds DOCX support
    /// on top of the CSV / XLSX / PDF pipeline already served by
    /// <c>POST /{code}/generate</c>. The endpoint materialises the report as
    /// CSV via <see cref="IReportingService"/>, parses the CSV into headers +
    /// rows, then dispatches through <see cref="IReportExportSelector"/> to
    /// the exporter matching <paramref name="format"/>. CSV / XLSX / PDF
    /// re-render the matrix; DOCX is the format unlocked by the new pipeline.
    /// </summary>
    /// <param name="code">Stable report code (e.g. <c>AUDIT_LOG</c>).</param>
    /// <param name="format">Desired output format: <c>Csv</c> | <c>Xlsx</c> | <c>Docx</c> | <c>Pdf</c>. Defaults to <c>Xlsx</c>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with a <see cref="FileContentResult"/> carrying the rendered
    /// bytes, the canonical MIME type for the format, and a
    /// <c>Content-Disposition: attachment; filename="{code}-{date}.{ext}"</c>
    /// header. 404 when the code is unknown; 400 on validation failure; 501
    /// when no exporter is registered for the requested format.
    /// </returns>
    [HttpGet("{code}/export")]
    [Authorize(Policy = AuthorizationComposition.CnasDecider)]
    public async Task<IActionResult> ExportAsync(
        [FromRoute] string code,
        [FromQuery] ReportExportFormat format = ReportExportFormat.Xlsx,
        CancellationToken cancellationToken = default)
    {
        // 1. Always render via CSV first — the existing service guarantees
        //    a byte-stream that we can deterministically parse into a row
        //    matrix, regardless of which terminal format the caller wants.
        var csvResult = await _reports.GenerateAsync(code, "{}", ExportFormat.Csv, cancellationToken)
            .ConfigureAwait(false);
        if (csvResult.IsFailure)
        {
            return MapFailureBare(csvResult.ErrorCode, csvResult.ErrorMessage);
        }

        // 2. Parse the CSV into a (headers + rows) shape that the universal
        //    exporters consume directly. Use the same reader settings the
        //    CsvReportExporter uses on the write path so a CSV-in / CSV-out
        //    round-trip is lossless.
        var input = await BuildReportExportInputAsync(code, csvResult.Value, cancellationToken)
            .ConfigureAwait(false);

        // 3. Dispatch through the universal exporter pipeline.
        var exportResult = await _exportSelector.ExportAsync(format, input, cancellationToken)
            .ConfigureAwait(false);
        if (exportResult.IsFailure)
        {
            return exportResult.ErrorCode == ErrorCodes.ExportFormatNotSupported
                       || exportResult.ErrorCode == ErrorCodes.ExportDocxNotAvailable
                ? StatusCode(StatusCodes.Status501NotImplemented,
                    new ProblemDetails
                    {
                        Title = "Export format not available",
                        Detail = exportResult.ErrorMessage,
                        Status = StatusCodes.Status501NotImplemented,
                        Extensions = { ["format"] = format.ToString() },
                    })
                : MapFailureBare(exportResult.ErrorCode, exportResult.ErrorMessage);
        }

        var stamp = _clock.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var fileName = $"{code}-{stamp}{exportResult.Value.FileExtension}";
        return File(exportResult.Value.Bytes, exportResult.Value.ContentType, fileName);
    }

    /// <summary>
    /// Parses the CSV bytes produced by <see cref="IReportingService"/> into a
    /// <see cref="ReportExportInputDto"/> the universal exporter pipeline can
    /// consume. The first record is the header row; every subsequent record
    /// is a data row whose cell count is normalised to match the header
    /// width.
    /// </summary>
    /// <param name="reportCode">Report code (used as the document title).</param>
    /// <param name="csvStream">CSV stream from the reporting service.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>A populated <see cref="ReportExportInputDto"/>.</returns>
    private static async Task<ReportExportInputDto> BuildReportExportInputAsync(
        string reportCode,
        Stream csvStream,
        CancellationToken cancellationToken)
    {
        // Reset the stream to position 0 in case the producer left it at the
        // end; CsvReader requires a readable seekable stream to enumerate.
        if (csvStream.CanSeek)
        {
            csvStream.Position = 0;
        }

        using var reader = new StreamReader(csvStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            BadDataFound = null,
        };
        using var csv = new CsvReader(reader, config);

        var rows = new List<IReadOnlyList<string>>();
        IReadOnlyList<string>? headers = null;
        while (await csv.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = csv.Parser.Record ?? System.Array.Empty<string>();
            if (headers is null)
            {
                headers = record.ToArray();
                continue;
            }
            // Normalise row length to match the headers — pad short rows with
            // empty cells; truncate long rows. The validator on the receiving
            // exporter cares about coherence, not exotic widths.
            var normalised = new string[headers.Count];
            for (int i = 0; i < headers.Count; i++)
            {
                normalised[i] = i < record.Length ? record[i] : string.Empty;
            }
            rows.Add(normalised);
        }

        var columns = (headers ?? FallbackHeaders)
            .Select(h => new ReportExportColumnDto(h))
            .ToArray();
        return new ReportExportInputDto(
            ReportTitle: reportCode,
            Columns: columns,
            Rows: rows);
    }

    /// <summary>
    /// Encodes the per-report parameter dictionary as a JSON object. Empty / null maps are
    /// serialised as <c>{}</c> — the service tolerates either input and reads the same
    /// known keys with safe defaults.
    /// </summary>
    /// <param name="parameters">Optional parameter dictionary, may be null.</param>
    /// <returns>A JSON object literal (never null).</returns>
    private static string SerialiseParameters(IReadOnlyDictionary<string, string?>? parameters)
    {
        // JsonSerializer.Serialize(null) emits the literal "null" — we want "{}" instead so
        // the service's TryParseParameters consumes an empty object rather than failing the
        // JSON-element kind check.
        return parameters is null || parameters.Count == 0
            ? "{}"
            : JsonSerializer.Serialize(parameters);
    }

    /// <summary>
    /// Maps an <see cref="ExportFormat"/> to its canonical MIME type. The XLSX value is the
    /// OpenXML spreadsheet MIME registered by Microsoft; the CSV value is text/csv per
    /// RFC 7111.
    /// </summary>
    /// <param name="format">Requested export format.</param>
    /// <returns>The canonical MIME type for the format.</returns>
    private static string ContentTypeFor(ExportFormat format) => format switch
    {
        ExportFormat.Csv => "text/csv",
        ExportFormat.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ExportFormat.Pdf => "application/pdf",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// Maps an <see cref="ExportFormat"/> to its conventional filename extension. Used to
    /// derive <c>FileDownloadName</c> so the browser's Save-As
    /// dialog suggests the right type without the user having to retype it.
    /// </summary>
    /// <param name="format">Requested export format.</param>
    /// <returns>The dotted file extension including the leading dot (e.g. <c>.csv</c>).</returns>
    private static string ExtensionFor(ExportFormat format) => format switch
    {
        ExportFormat.Csv => ".csv",
        ExportFormat.Xlsx => ".xlsx",
        ExportFormat.Pdf => ".pdf",
        _ => ".bin",
    };

    /// <summary>Maps a <see cref="Result{T}"/> failure to an <see cref="ActionResult{T}"/>.</summary>
    /// <typeparam name="T">The DTO type the action would have returned on success.</typeparam>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 400 ProblemDetails as appropriate.</returns>
    private ActionResult<T> MapFailureGeneric<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Maps a non-generic <see cref="Result"/> failure to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 400 ProblemDetails as appropriate.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>404 NotFound, or 400 BadRequest for everything else.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.ReportTooLarge => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
