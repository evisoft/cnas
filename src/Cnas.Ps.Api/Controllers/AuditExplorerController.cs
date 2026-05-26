using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0193 / TOR SEC 052 — admin audit explorer REST surface. Three endpoints
/// (search / export / archive-import), all gated on the <c>cnas-admin</c>
/// role. The Blazor explorer UI is deferred — this controller is the server
/// backend the UI will eventually consume.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST /api/admin/audit/search</c> — paged QBE-filterable search.</item>
///   <item><c>POST /api/admin/audit/export?format=csv|xlsx|pdf</c> — export via R0226.</item>
///   <item><c>POST /api/admin/audit/archives/{archiveKey}/import</c> — re-attach an archived batch (R0188).</item>
/// </list>
/// </para>
/// <para>
/// <b>Failure mapping.</b>
/// <list type="bullet">
///   <item>Success → 200 (search/import) or 200 + file (export).</item>
///   <item><see cref="ErrorCodes.QueryTooBroad"/> → 422 ProblemDetails with budget verdict in <c>extensions["budget"]</c>.</item>
///   <item><see cref="ErrorCodes.ExportFormatNotSupported"/> → 501 ProblemDetails.</item>
///   <item><see cref="ErrorCodes.NotFound"/> → 404.</item>
///   <item>QBE_* → 400 ProblemDetails.</item>
///   <item>Anything else → 400 ProblemDetails.</item>
/// </list>
/// </para>
/// <para>
/// <b>Authorisation.</b> <c>cnas-admin</c> role only — the audit log carries
/// data produced by every other registry and exposing it broadly would
/// re-leak PII the audit infrastructure was designed to redact.
/// </para>
/// </remarks>
/// <param name="svc">Underlying audit explorer service (per-request scope).</param>
[ApiController]
[Authorize(Roles = "cnas-admin")]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/audit")]
public sealed class AuditExplorerController(IAuditExplorerService svc) : ControllerBase
{
    /// <summary>Underlying service.</summary>
    private readonly IAuditExplorerService _svc = svc;

    /// <summary>Stable ProblemDetails <c>type</c> URI for the "query too broad" failure.</summary>
    private const string QueryTooBroadProblemType = "https://cnas/queries/too-broad";

    /// <summary>Stable ProblemDetails <c>type</c> URI for the unimplemented-format failure.</summary>
    private const string ExportNotImplementedProblemType = "https://cnas/exports/not-implemented";

    /// <summary>Stable ProblemDetails <c>type</c> URI for invalid QBE envelopes.</summary>
    private const string QbeInvalidProblemType = "https://cnas/qbe/invalid";

    /// <summary>
    /// Paged QBE-filterable search over the audit-log table. Body is the
    /// <see cref="AuditLogSearchInput"/> envelope; response is an
    /// <see cref="AuditLogPageDto"/>.
    /// </summary>
    /// <param name="input">Search envelope; null short-circuits to a default empty filter.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 + DTO on success; 422 / 400 / 401 / 403 per the failure mapping.</returns>
    [HttpPost("search")]
    [Consumes("application/json")]
    public async Task<IActionResult> SearchAsync(
        [FromBody] AuditLogSearchInput? input,
        CancellationToken cancellationToken = default)
    {
        input ??= new AuditLogSearchInput();
        var result = await _svc.SearchAsync(input, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        if (string.Equals(result.ErrorCode, ErrorCodes.QueryTooBroad, StringComparison.Ordinal))
        {
            return QueryTooBroadProblem(result.ErrorMessage, _svc.LastBudgetVerdict);
        }

        if (IsQbeFailureCode(result.ErrorCode))
        {
            return QbeBadRequest(result.ErrorCode!, result.ErrorMessage);
        }

        return Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Exports the QBE-filtered rows to the requested format via the R0226
    /// universal exporter.
    /// </summary>
    /// <param name="format">Output format — <c>csv</c> / <c>xlsx</c> / <c>pdf</c>.</param>
    /// <param name="input">Search envelope; null short-circuits to a default empty filter.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 + FileResult on success; 422 / 501 / 400 per the failure mapping.</returns>
    [HttpPost("export")]
    public async Task<IActionResult> ExportAsync(
        [FromQuery] string format = "csv",
        [FromBody] AuditLogSearchInput? input = null,
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

        input ??= new AuditLogSearchInput();
        var result = await _svc.ExportAsync(input, parsedFormat, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return File(
                fileContents: result.Value.Content,
                contentType: result.Value.ContentType,
                fileDownloadName: result.Value.SuggestedFileName);
        }

        if (string.Equals(result.ErrorCode, ErrorCodes.QueryTooBroad, StringComparison.Ordinal))
        {
            return QueryTooBroadProblem(result.ErrorMessage, _svc.LastBudgetVerdict);
        }

        if (string.Equals(result.ErrorCode, ErrorCodes.ExportFormatNotSupported, StringComparison.Ordinal))
        {
            return ExportNotImplementedProblem(FormatTag(parsedFormat));
        }

        if (IsQbeFailureCode(result.ErrorCode))
        {
            return QbeBadRequest(result.ErrorCode!, result.ErrorMessage);
        }

        return Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Re-attaches the records of an archived audit batch onto the live
    /// AuditLog table. The audit pipeline writes a Critical
    /// <c>AUDIT.ARCHIVE.IMPORTED</c> row on every successful call.
    /// </summary>
    /// <param name="archiveKey">Opaque archive identifier (URL-encoded; route parameter).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 + summary on success; 404 when the archive is missing.</returns>
    [HttpPost("archives/{archiveKey}/import")]
    public async Task<IActionResult> ImportArchiveAsync(
        [FromRoute] string archiveKey,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ImportArchiveAsync(archiveKey, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }
        if (string.Equals(result.ErrorCode, ErrorCodes.NotFound, StringComparison.Ordinal))
        {
            return NotFound(new ProblemDetails
            {
                Title = "Audit archive not found.",
                Detail = result.ErrorMessage,
                Status = StatusCodes.Status404NotFound,
            });
        }
        return Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Parses the caller-supplied <c>format</c> string to the closed
    /// <see cref="ExportFormat"/> enum. Case-insensitive; default is CSV.
    /// </summary>
    /// <param name="value">Caller-supplied format name.</param>
    /// <param name="format">Parsed enum.</param>
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
            case "csv": format = ExportFormat.Csv; return true;
            case "xlsx": format = ExportFormat.Xlsx; return true;
            case "pdf": format = ExportFormat.Pdf; return true;
            default: format = ExportFormat.Csv; return false;
        }
    }

    /// <summary>Short tag projection mirroring <c>GridExporter.FormatTag</c>.</summary>
    /// <param name="format">Format value.</param>
    /// <returns>Lowercased tag.</returns>
    private static string FormatTag(ExportFormat format) =>
        format switch
        {
            ExportFormat.Csv => "csv",
            ExportFormat.Xlsx => "xlsx",
            ExportFormat.Pdf => "pdf",
            _ => format.ToString().ToLowerInvariant(),
        };

    /// <summary>Returns true when <paramref name="code"/> is a QBE failure code.</summary>
    /// <param name="code">Error code from the service-layer Result.</param>
    /// <returns><c>true</c> when the code is one of the <c>QBE_*</c> family.</returns>
    private static bool IsQbeFailureCode(string? code) =>
        code is ErrorCodes.QbeFieldNotQueryable
            or ErrorCodes.QbeOperatorNotSupported
            or ErrorCodes.QbeValueInvalid
            or ErrorCodes.QbeInvalidCombinator
            or ErrorCodes.QbeRegistryUnknown;

    /// <summary>
    /// Builds the 422 ProblemDetails for a too-broad query. Mirrors the
    /// contract used by the other budget-gated controllers so the UI can
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
        problem.Extensions["budget"] = ToBudgetDto(verdict);
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status422UnprocessableEntity,
            ContentTypes = { "application/problem+json" },
        };
    }

    /// <summary>
    /// Builds the 501 ProblemDetails for an export format that has no
    /// renderer wired. Format name lives in <c>extensions["format"]</c>.
    /// </summary>
    /// <param name="format">Format name.</param>
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
    /// Builds the 400 ProblemDetails for a QBE failure. Carries the stable
    /// error code in <c>extensions["errorCode"]</c> so the UI can branch.
    /// </summary>
    /// <param name="code">Stable QBE error code.</param>
    /// <param name="detail">Service-layer human message.</param>
    /// <returns>The 400 ObjectResult.</returns>
    private ObjectResult QbeBadRequest(string code, string? detail)
    {
        var problem = new ProblemDetails
        {
            Type = QbeInvalidProblemType,
            Title = "The QBE filter could not be applied.",
            Detail = detail,
            Status = StatusCodes.Status400BadRequest,
        };
        problem.Extensions["errorCode"] = code;
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" },
        };
    }

    /// <summary>
    /// Translates a service-layer <see cref="QueryBudgetVerdict"/> to the wire
    /// DTO. A <c>null</c> verdict surfaces as a structurally-empty DTO.
    /// </summary>
    /// <param name="verdict">Verdict; nullable.</param>
    /// <returns>The wire DTO.</returns>
    private static QueryBudgetVerdictDto ToBudgetDto(QueryBudgetVerdict? verdict)
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
}
