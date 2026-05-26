using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// UC10 + R0671 continuation — Decision workflow REST surface. Approve / reject paths
/// live on a dedicated route table; the registry list (R0671 continuation) ships under
/// the same controller so the wire surface stays cohesive with the audit + documents
/// explorers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST /api/decisions/search</c> — paged QBE-filterable registry list (R0671 continuation).</item>
/// </list>
/// </para>
/// <para>
/// <b>Failure mapping.</b>
/// <list type="bullet">
///   <item>Success → 200 + page DTO.</item>
///   <item><see cref="ErrorCodes.QueryTooBroad"/> → 422 ProblemDetails with budget verdict in <c>extensions["budget"]</c>.</item>
///   <item>QBE_* → 400 ProblemDetails.</item>
///   <item>Anything else → 400 ProblemDetails.</item>
/// </list>
/// </para>
/// </remarks>
/// <param name="decisions">Underlying decision workflow service (per-request scope).</param>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/decisions")]
public sealed class DecisionsController(IDecisionWorkflowService decisions) : ControllerBase
{
    /// <summary>Underlying service.</summary>
    private readonly IDecisionWorkflowService _decisions = decisions;

    /// <summary>Stable ProblemDetails <c>type</c> URI for the "query too broad" failure.</summary>
    private const string QueryTooBroadProblemType = "https://cnas/queries/too-broad";

    /// <summary>Stable ProblemDetails <c>type</c> URI for invalid QBE envelopes.</summary>
    private const string QbeInvalidProblemType = "https://cnas/qbe/invalid";

    /// <summary>
    /// R0671 continuation — paged QBE-filterable list of decisions (projected from the
    /// Dossier + parent ServiceApplication aggregate).
    /// </summary>
    /// <param name="input">Search envelope; null short-circuits to a default empty filter.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 + page DTO on success; 422 / 400 per the failure mapping.</returns>
    [HttpPost("search")]
    [Consumes("application/json")]
    public async Task<IActionResult> SearchAsync(
        [FromBody] DecisionsListInput? input,
        CancellationToken cancellationToken = default)
    {
        input ??= new DecisionsListInput();
        var result = await _decisions.ListAsync(input, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        if (string.Equals(result.ErrorCode, ErrorCodes.QueryTooBroad, StringComparison.Ordinal))
        {
            return QueryTooBroadProblem(result.ErrorMessage, _decisions.LastBudgetVerdict);
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
    /// <param name="verdict">The most recent budget verdict.</param>
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
