using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Exports;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0226 / TOR UI 013 — universal grid-export REST surface. Today exposes one
/// endpoint (<c>POST /api/grid-exports/solicitants</c>) as the canonical proof
/// point; other registries opt in via follow-up batches.
/// </summary>
/// <remarks>
/// <para>
/// <b>Format dispatch.</b> The endpoint accepts <c>format=csv|xlsx|pdf</c> as a
/// query-string argument and maps it to the
/// <see cref="ExportFormat"/> enum. Unknown formats surface as 400
/// ProblemDetails (the validator's job) so the service layer sees only the
/// closed enum set.
/// </para>
/// <para>
/// <b>Result-envelope mapping.</b>
/// <list type="bullet">
///   <item>Success → 200 with the rendered file body + <c>Content-Disposition</c>.</item>
///   <item><see cref="ErrorCodes.QueryTooBroad"/> → 422 ProblemDetails with the budget verdict in <c>extensions["budget"]</c>.</item>
///   <item><see cref="ErrorCodes.ExportTooLarge"/> → 422 ProblemDetails with the row count in <c>extensions["rowCount"]</c>.</item>
///   <item><see cref="ErrorCodes.ExportFormatNotSupported"/> → 501 ProblemDetails with the format name in <c>extensions["format"]</c>.</item>
///   <item>Anything else → 400 ProblemDetails.</item>
/// </list>
/// </para>
/// <para>
/// <b>Authorisation.</b> Same role gate as the underlying Solicitant list
/// endpoint (<see cref="SolicitantsController"/>) — only authenticated CNAS
/// users / admins / deciders can request an export.
/// </para>
/// </remarks>
/// <param name="solicitants">Per-request grid-export façade for Solicitants.</param>
[ApiController]
[Authorize(Roles = "cnas-user,cnas-decider,cnas-admin")]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/grid-exports")]
public sealed class GridExportsController(ISolicitantGridExportService solicitants) : ControllerBase
{
    /// <summary>Solicitant export façade.</summary>
    private readonly ISolicitantGridExportService _solicitants = solicitants;

    /// <summary>
    /// Stable ProblemDetails <c>type</c> URI for the "query too broad" failure
    /// mode. Identical to <see cref="SolicitantsController"/> so the UI can
    /// share its refinement-prompt rendering pipeline.
    /// </summary>
    private const string QueryTooBroadProblemType = "https://cnas/queries/too-broad";

    /// <summary>
    /// Stable ProblemDetails <c>type</c> URI for the "export too large"
    /// failure mode (row cap exceeded). UI matches on this string to render
    /// the "narrow your filter and retry" prompt.
    /// </summary>
    private const string ExportTooLargeProblemType = "https://cnas/exports/too-large";

    /// <summary>
    /// Stable ProblemDetails <c>type</c> URI for the
    /// "export format not implemented" failure mode. Identical to
    /// <see cref="PublicCatalogController"/> so the UI can hide the
    /// corresponding download button.
    /// </summary>
    private const string ExportNotImplementedProblemType = "https://cnas/exports/not-implemented";

    /// <summary>
    /// Exports the filtered Solicitant list to the requested format. The body
    /// re-uses <see cref="SolicitantListQueryInput"/> so the UI does not need
    /// to learn a second filter shape.
    /// </summary>
    /// <param name="format">Output format: <c>csv</c>, <c>xlsx</c>, or <c>pdf</c>.</param>
    /// <param name="input">
    /// Same filter envelope used by the list endpoint. <c>null</c> means "no
    /// filters" — the budget guard will refuse the export in that case.
    /// </param>
    /// <param name="language">ISO-639-1 language code; default <c>ro</c>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The HTTP response (see remarks).</returns>
    [HttpPost("solicitants")]
    public async Task<IActionResult> ExportSolicitantsAsync(
        [FromQuery] string format = "csv",
        [FromBody] SolicitantListQueryInput? input = null,
        [FromQuery] string? language = "ro",
        CancellationToken cancellationToken = default)
    {
        if (!TryParseFormat(format, out var parsedFormat))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Unknown export format.",
                Detail = $"Format '{format}' is not supported; expected one of csv, xlsx, pdf.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        var effectiveInput = input ?? new SolicitantListQueryInput();
        var result = await _solicitants
            .ExportAsync(effectiveInput, parsedFormat, language, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return File(
                fileContents: result.Value.Content,
                contentType: result.Value.ContentType,
                fileDownloadName: result.Value.SuggestedFileName);
        }

        if (string.Equals(result.ErrorCode, ErrorCodes.QueryTooBroad, StringComparison.Ordinal))
        {
            return QueryTooBroadProblem(result.ErrorMessage, _solicitants.LastBudgetVerdict);
        }

        if (string.Equals(result.ErrorCode, ErrorCodes.ExportTooLarge, StringComparison.Ordinal))
        {
            return ExportTooLargeProblem(result.ErrorMessage);
        }

        if (string.Equals(result.ErrorCode, ErrorCodes.ExportFormatNotSupported, StringComparison.Ordinal))
        {
            return ExportNotImplementedProblem(FormatTag(parsedFormat));
        }

        return BadRequest(new ProblemDetails
        {
            Title = "Export failed.",
            Detail = result.ErrorMessage,
            Status = StatusCodes.Status400BadRequest,
        });
    }

    /// <summary>
    /// Parses the caller-supplied <c>format</c> string to the closed
    /// <see cref="ExportFormat"/> enum. Case-insensitive.
    /// </summary>
    /// <param name="value">Caller-supplied format name.</param>
    /// <param name="format">Parsed enum (default <see cref="ExportFormat.Csv"/> on null/empty).</param>
    /// <returns><c>true</c> on a known format; <c>false</c> otherwise.</returns>
    private static bool TryParseFormat(string? value, out ExportFormat format)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            format = ExportFormat.Csv;
            return true;
        }
        switch (value.Trim().ToLowerInvariant())
        {
            case "csv":  format = ExportFormat.Csv; return true;
            case "xlsx": format = ExportFormat.Xlsx; return true;
            case "pdf":  format = ExportFormat.Pdf; return true;
            default:     format = ExportFormat.Csv; return false;
        }
    }

    /// <summary>Short tag projection mirroring <c>GridExporter.FormatTag</c>.</summary>
    /// <param name="format">Format value.</param>
    /// <returns>Lowercased tag.</returns>
    private static string FormatTag(ExportFormat format) =>
        format switch
        {
            ExportFormat.Csv  => "csv",
            ExportFormat.Xlsx => "xlsx",
            ExportFormat.Pdf  => "pdf",
            _ => format.ToString().ToLowerInvariant(),
        };

    /// <summary>
    /// Builds the 422 ProblemDetails for a too-broad query. Mirrors the
    /// contract used by <see cref="SolicitantsController"/> so the UI can
    /// share its rendering pipeline.
    /// </summary>
    /// <param name="detail">Human-readable detail from the service failure.</param>
    /// <param name="verdict">The most recent budget verdict; nullable.</param>
    /// <returns>The 422 ObjectResult.</returns>
    private ObjectResult QueryTooBroadProblem(string? detail, QueryBudgetVerdict? verdict)
    {
        var problem = new ProblemDetails
        {
            Type = QueryTooBroadProblemType,
            Title = "The query is too broad and would exceed the registry budget.",
            Detail = detail,
            Status = StatusCodes.Status422UnprocessableEntity,
        };
        problem.Extensions["budget"] = ToDto(verdict);
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status422UnprocessableEntity,
            ContentTypes = { "application/problem+json" },
        };
    }

    /// <summary>
    /// Builds the 422 ProblemDetails for an over-the-cap export. The row
    /// count parsed out of the service-layer failure detail (when present)
    /// surfaces in <c>extensions["rowCount"]</c> so the UI can render a
    /// specific "narrow your filter — N rows exceeds 50,000" prompt.
    /// </summary>
    /// <param name="detail">Service-layer failure message (may carry row count).</param>
    /// <returns>The 422 ObjectResult.</returns>
    private ObjectResult ExportTooLargeProblem(string? detail)
    {
        var problem = new ProblemDetails
        {
            Type = ExportTooLargeProblemType,
            Title = "The export is too large.",
            Detail = detail,
            Status = StatusCodes.Status422UnprocessableEntity,
        };
        problem.Extensions["rowCount"] = ExtractRowCount(detail);
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status422UnprocessableEntity,
            ContentTypes = { "application/problem+json" },
        };
    }

    /// <summary>
    /// Builds the 501 ProblemDetails for an export-format that has no
    /// renderer wired. The format name lives in <c>extensions["format"]</c>
    /// so a single URI covers all unwired formats.
    /// </summary>
    /// <param name="format">Format name (e.g. <c>xlsx</c>).</param>
    /// <returns>The 501 ObjectResult.</returns>
    private ObjectResult ExportNotImplementedProblem(string format)
    {
        var problem = new ProblemDetails
        {
            Type = ExportNotImplementedProblemType,
            Title = "This export format is not implemented yet.",
            Detail = $"No renderer is wired for format '{format}'.",
            Status = StatusCodes.Status501NotImplemented,
        };
        problem.Extensions["format"] = format;
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status501NotImplemented,
            ContentTypes = { "application/problem+json" },
        };
    }

    /// <summary>
    /// Translates a service-layer <see cref="QueryBudgetVerdict"/> to the
    /// wire DTO. A <c>null</c> verdict surfaces as a structurally-empty DTO so
    /// callers can rely on the shape of <c>extensions["budget"]</c>.
    /// </summary>
    /// <param name="verdict">Verdict carried back from the service; nullable.</param>
    /// <returns>The wire DTO.</returns>
    private static QueryBudgetVerdictDto ToDto(QueryBudgetVerdict? verdict)
    {
        if (verdict is null)
        {
            return new QueryBudgetVerdictDto(string.Empty, 0, 0, Array.Empty<QueryBudgetRefinementHintDto>());
        }
        var hints = verdict.Hints
            .Select(h => new QueryBudgetRefinementHintDto(h.FieldName, h.Severity, h.Reason))
            .ToList();
        return new QueryBudgetVerdictDto(
            verdict.Registry,
            verdict.EstimatedRowCount,
            verdict.Budget,
            hints);
    }

    /// <summary>
    /// Attempts to parse the row count from the service-layer failure detail
    /// (<c>"Row count 60000 exceeds …"</c>). Returns 0 when the format does
    /// not match — the controller still surfaces a 422 either way.
    /// </summary>
    /// <param name="detail">Service-layer failure message.</param>
    /// <returns>Best-effort row count.</returns>
    private static int ExtractRowCount(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return 0;
        }
        // Walk the string and collect the FIRST integer run; mechanical but
        // robust against minor wording changes in the service-layer message.
        var i = 0;
        while (i < detail.Length && !char.IsDigit(detail[i])) { i++; }
        var start = i;
        while (i < detail.Length && char.IsDigit(detail[i])) { i++; }
        if (start == i)
        {
            return 0;
        }
        return int.TryParse(detail.AsSpan(start, i - start), out var n) ? n : 0;
    }
}
