using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>UC11 — Document upload / download / list REST surface.</summary>
/// <remarks>
/// <para>
/// <b>Endpoints.</b>
/// <list type="bullet">
///   <item><c>POST /api/documents/upload</c> — magic-byte-validated citizen upload (SEC 010).</item>
///   <item><c>GET  /api/documents/{id}/download-url</c> — short-lived presigned MinIO URL.</item>
///   <item><c>POST /api/documents/search</c> — paged QBE-filterable registry list (R0671 continuation).</item>
/// </list>
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/documents")]
public sealed class DocumentsController(IDocumentService documents) : ControllerBase
{
    /// <summary>Underlying service.</summary>
    private readonly IDocumentService _documents = documents;

    /// <summary>Stable ProblemDetails <c>type</c> URI for the "query too broad" failure.</summary>
    private const string QueryTooBroadProblemType = "https://cnas/queries/too-broad";

    /// <summary>Stable ProblemDetails <c>type</c> URI for invalid QBE envelopes.</summary>
    private const string QbeInvalidProblemType = "https://cnas/qbe/invalid";

    /// <summary>
    /// Upload an attachment. The server validates the file's magic bytes (SEC 010).
    /// Throttled by the stricter <see cref="RateLimitingPolicies.Upload"/> policy at
    /// the method level so abuse of the upload path can't exhaust storage bandwidth
    /// even if the caller stays under the wider <see cref="RateLimitingPolicies.Authenticated"/>
    /// allowance applied at the controller level. Method-level attributes take
    /// precedence over controller-level ones for limiter resolution.
    /// </summary>
    /// <param name="file">Uploaded multipart form file.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>Sqid-encoded document id on success.</returns>
    [HttpPost("upload")]
    [EnableRateLimiting(RateLimitingPolicies.Upload)]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<ActionResult<string>> UploadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        await using var stream = file.OpenReadStream();
        var result = await _documents.UploadAsync(file.FileName, stream, file.ContentType, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : Problem(result.ErrorMessage, statusCode: 400);
    }

    /// <summary>Issue a time-limited download URL for a document.</summary>
    /// <param name="id">Sqid-encoded document id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The presigned URL string on success; 404 otherwise.</returns>
    [HttpGet("{id}/download-url")]
    public async Task<ActionResult<Uri>> DownloadUrlAsync(string id, CancellationToken cancellationToken)
    {
        var result = await _documents.GetDownloadUrlAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value.ToString()) : NotFound();
    }

    /// <summary>
    /// R0671 continuation — paged QBE-filterable list of documents in the registry.
    /// Mirrors the audit-explorer search contract: 200 on success, 422 on budget
    /// refusal (verdict carried in <c>extensions["budget"]</c>), 400 on a QBE
    /// envelope rejection.
    /// </summary>
    /// <param name="input">Search envelope; null short-circuits to a default empty filter.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 + page DTO on success; 422 / 400 per the failure mapping.</returns>
    [HttpPost("search")]
    [Consumes("application/json")]
    public async Task<IActionResult> SearchAsync(
        [FromBody] DocumentsListInput? input,
        CancellationToken cancellationToken = default)
    {
        input ??= new DocumentsListInput();
        var result = await _documents.ListAsync(input, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        if (string.Equals(result.ErrorCode, ErrorCodes.QueryTooBroad, StringComparison.Ordinal))
        {
            return QueryTooBroadProblem(result.ErrorMessage, _documents.LastBudgetVerdict);
        }

        if (IsQbeFailureCode(result.ErrorCode))
        {
            return QbeBadRequest(result.ErrorCode!, result.ErrorMessage);
        }

        return Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>Returns true when <paramref name="code"/> is one of the <c>QBE_*</c> family.</summary>
    /// <param name="code">Error code from the service-layer Result.</param>
    /// <returns><c>true</c> when the code is a QBE failure code.</returns>
    private static bool IsQbeFailureCode(string? code) =>
        code is ErrorCodes.QbeFieldNotQueryable
            or ErrorCodes.QbeOperatorNotSupported
            or ErrorCodes.QbeValueInvalid
            or ErrorCodes.QbeInvalidCombinator
            or ErrorCodes.QbeRegistryUnknown;

    /// <summary>Builds the 422 ProblemDetails for a too-broad query.</summary>
    /// <param name="detail">Human-readable detail from the service failure.</param>
    /// <param name="verdict">The most recent budget verdict carried on the service instance.</param>
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

    /// <summary>Builds the 400 ProblemDetails for a QBE failure.</summary>
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

    /// <summary>Translates a service-layer verdict to the wire DTO.</summary>
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
