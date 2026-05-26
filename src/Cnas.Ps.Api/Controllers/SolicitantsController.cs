using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0167 / TOR CF 01.06 / CF 03.07-08 — Solicitant registry list REST surface. The list
/// endpoint consults <see cref="IQueryBudgetService"/> through
/// <see cref="ISolicitantService"/>; an over-budget query surfaces as a 422
/// ProblemDetails carrying a structured refinement prompt in
/// <c>extensions["budget"]</c>.
/// </summary>
/// <param name="svc">Underlying list façade (per-request scope).</param>
[ApiController]
[Authorize(Roles = "cnas-user,cnas-admin")]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/solicitants")]
public sealed class SolicitantsController(ISolicitantService svc) : ControllerBase
{
    private readonly ISolicitantService _svc = svc;

    /// <summary>
    /// Stable ProblemDetails <c>type</c> URI for the "query too broad" failure mode.
    /// Stable across versions — UI code matches on this string to render the
    /// refinement prompt.
    /// </summary>
    private const string QueryTooBroadProblemType = "https://cnas/queries/too-broad";

    /// <summary>
    /// R0623 / TOR CF 13.04 — preview the OPEN-state reference scan for a
    /// Solicitant. Surfaced for the admin UI so it can render the
    /// block-or-allow verdict (and the per-table breakdown) BEFORE the operator
    /// attempts a soft-delete via <see cref="DeactivateAsync"/>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the Solicitant.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with <see cref="SolicitantReferenceScanDto"/> on success; 400
    /// ProblemDetails on an invalid Sqid; 404 when the Solicitant is missing
    /// or already deactivated.
    /// </returns>
    [HttpGet("{sqid}/reference-scan")]
    [Authorize(Policy = AuthorizationComposition.CnasAdmin)]
    public async Task<ActionResult<SolicitantReferenceScanDto>> GetReferenceScanAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ScanReferencesAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }
        if (string.Equals(result.ErrorCode, ErrorCodes.NotFound, StringComparison.Ordinal))
        {
            return NotFound();
        }
        return Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// R0623 / TOR CF 13.04 — soft-deactivates a Solicitant after the
    /// <c>ISolicitantReferenceGuard</c> confirms no OPEN-state references
    /// would be orphaned. A non-zero open-record total surfaces as 409
    /// ProblemDetails with the stable error code
    /// <see cref="ErrorCodes.SolicitantReferencedByOpenRecords"/>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the Solicitant.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 204 No Content on success; 400 ProblemDetails on an invalid Sqid;
    /// 404 when the Solicitant is missing; 409 ProblemDetails with stable
    /// code <see cref="ErrorCodes.SolicitantReferencedByOpenRecords"/> when
    /// the guard reports open references.
    /// </returns>
    [HttpPost("{sqid}/deactivate")]
    [Authorize(Policy = AuthorizationComposition.CnasAdmin)]
    public async Task<IActionResult> DeactivateAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.DeactivateAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return NoContent();
        }
        if (string.Equals(result.ErrorCode, ErrorCodes.NotFound, StringComparison.Ordinal))
        {
            return NotFound();
        }
        if (string.Equals(
                result.ErrorCode,
                ErrorCodes.SolicitantReferencedByOpenRecords,
                StringComparison.Ordinal))
        {
            var problem = new ProblemDetails
            {
                Type = "https://cnas/solicitants/referenced-by-open-records",
                Title = "The Solicitant is referenced by open records and cannot be deactivated.",
                Detail = result.ErrorMessage,
                Status = StatusCodes.Status409Conflict,
            };
            problem.Extensions["errorCode"] = result.ErrorCode;
            return new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status409Conflict,
                ContentTypes = { "application/problem+json" },
            };
        }
        return Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Paged list of Solicitants. Returns 422 when the filter set is too broad.
    /// </summary>
    /// <param name="query">Optional free-text query (display-name substring).</param>
    /// <param name="createdFromUtc">Inclusive lower bound on creation timestamp.</param>
    /// <param name="createdToUtc">Exclusive upper bound on creation timestamp.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Items per page (service clamps to [1, 200]).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with the paged list on success; 422 ProblemDetails carrying the budget
    /// verdict in <c>extensions["budget"]</c> when the query exceeds the registry
    /// budget; 400 ProblemDetails for other validation failures.
    /// </returns>
    [HttpGet]
    public async Task<ActionResult<PagedResult<SolicitantListItem>>> ListAsync(
        [FromQuery] string? query = null,
        [FromQuery] DateTime? createdFromUtc = null,
        [FromQuery] DateTime? createdToUtc = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var input = new SolicitantListQueryInput(query, createdFromUtc, createdToUtc, page, pageSize);
        var result = await _svc.ListAsync(input, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        if (string.Equals(result.ErrorCode, ErrorCodes.QueryTooBroad, StringComparison.Ordinal))
        {
            return QueryTooBroadProblem(result.ErrorMessage, _svc.LastBudgetVerdict);
        }

        return Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// R0163 — Query-By-Example variant of <see cref="ListAsync"/>. Accepts a typed
    /// <see cref="SolicitantSearchInput"/> body containing the paging inputs and an
    /// optional <see cref="QbeFilterDto"/> envelope. The QBE envelope is converted to
    /// a typed predicate server-side and applied BEFORE the budget guard runs.
    /// </summary>
    /// <param name="input">Search payload — paging fields + optional QBE envelope.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with the paged list on success; 422 ProblemDetails on budget refusal; 400
    /// ProblemDetails on malformed QBE input (carries <c>extensions["fieldName"]</c>
    /// for <see cref="ErrorCodes.QbeFieldNotQueryable"/>).
    /// </returns>
    [HttpPost("search")]
    [Consumes("application/json")]
    public async Task<ActionResult<SolicitantSearchOutput>> SearchAsync(
        [FromBody] SolicitantSearchInput? input,
        CancellationToken cancellationToken = default)
    {
        input ??= new SolicitantSearchInput();
        var listInput = new SolicitantListQueryInput(
            Q: input.Q,
            CreatedFromUtc: input.CreatedFromUtc,
            CreatedToUtc: input.CreatedToUtc,
            Page: input.Page,
            PageSize: input.PageSize);
        var qbe = ToDomain(input.Qbe);
        var result = await _svc.SearchAsync(listInput, qbe, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            // R0525 — wrap the page with the suggestion array so the UI can render
            // refinement prompts in one round-trip. Always shape-stable: an empty
            // Suggestions array when nothing applies.
            IReadOnlyList<Cnas.Ps.Contracts.Search.SearchSuggestionDto> suggestions =
                _svc.LastSuggestions ?? Array.Empty<Cnas.Ps.Contracts.Search.SearchSuggestionDto>();
            return Ok(new SolicitantSearchOutput(result.Value, suggestions));
        }

        if (string.Equals(result.ErrorCode, ErrorCodes.QueryTooBroad, StringComparison.Ordinal))
        {
            return QueryTooBroadProblem(result.ErrorMessage, _svc.LastBudgetVerdict);
        }

        // QBE-specific failure codes surface as 400 ProblemDetails. The QbeFieldNotQueryable
        // path also leaks the offending field name through extensions["fieldName"] so the
        // UI can highlight the bad row in the QBE form.
        if (IsQbeFailureCode(result.ErrorCode))
        {
            return QbeBadRequest(result.ErrorCode!, result.ErrorMessage, input.Qbe);
        }

        return Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Maps a wire <see cref="QbeFilterDto"/> to the server-side <see cref="QbeFilter"/>.
    /// Returns <see langword="null"/> when the wire envelope is missing or empty so the
    /// service can short-circuit to the legacy list path.
    /// </summary>
    /// <param name="dto">Wire envelope, nullable.</param>
    /// <returns>Mapped filter or <see langword="null"/>.</returns>
    private static QbeFilter? ToDomain(QbeFilterDto? dto)
    {
        if (dto is null || dto.Conditions is null || dto.Conditions.Count == 0)
        {
            return null;
        }
        var conditions = new List<QbeCondition>(dto.Conditions.Count);
        foreach (var c in dto.Conditions)
        {
            // Operator wire value is a stable PascalCase string. Enum.TryParse with
            // case-sensitive matching is intentional — a lowercase spelling should fail
            // here rather than be silently normalised.
            if (!Enum.TryParse<QbeOperator>(c.Operator, ignoreCase: false, out var op))
            {
                // Surface a sentinel value the converter can reject downstream — we keep
                // the original spelling so the converter's error message is precise.
                op = (QbeOperator)int.MinValue;
            }
            conditions.Add(new QbeCondition(c.FieldName, op, c.Value, c.Value2));
        }
        return new QbeFilter(
            string.IsNullOrEmpty(dto.Combinator) ? QbeFilter.CombinatorAnd : dto.Combinator,
            conditions);
    }

    /// <summary>Returns <see langword="true"/> when <paramref name="code"/> is a QBE failure code.</summary>
    /// <param name="code">Error code from the service-layer Result.</param>
    /// <returns><see langword="true"/> when the code is one of the <c>QBE_*</c> family.</returns>
    private static bool IsQbeFailureCode(string? code) =>
        code is ErrorCodes.QbeFieldNotQueryable
            or ErrorCodes.QbeOperatorNotSupported
            or ErrorCodes.QbeValueInvalid
            or ErrorCodes.QbeInvalidCombinator
            or ErrorCodes.QbeRegistryUnknown;

    /// <summary>
    /// Builds the 400 ProblemDetails for a QBE failure. For
    /// <see cref="ErrorCodes.QbeFieldNotQueryable"/> it also adds the offending field name
    /// to <c>extensions["fieldName"]</c> so the UI can render a field-targeted prompt
    /// without re-issuing the request.
    /// </summary>
    /// <param name="code">Stable QBE error code.</param>
    /// <param name="detail">Service-layer human message.</param>
    /// <param name="originalEnvelope">Wire envelope the request body carried — nullable.</param>
    /// <returns>The 400 ProblemDetails action result.</returns>
    private ObjectResult QbeBadRequest(string code, string? detail, QbeFilterDto? originalEnvelope)
    {
        var problem = new ProblemDetails
        {
            Type = "https://cnas/qbe/invalid",
            Title = "The QBE filter could not be applied.",
            Detail = detail,
            Status = StatusCodes.Status400BadRequest,
        };
        problem.Extensions["errorCode"] = code;

        if (string.Equals(code, ErrorCodes.QbeFieldNotQueryable, StringComparison.Ordinal)
            && originalEnvelope is { Conditions.Count: > 0 })
        {
            // Recover the offending field name from the service-layer message: the
            // converter formats the message as "Field 'X' is not queryable for ...". The
            // human message owns the canonical spelling so we extract it rather than
            // re-walking the envelope (the converter may have rejected the first OR the
            // n-th condition; we don't know which without re-resolving the schema).
            var fieldName = ExtractFieldName(detail) ?? originalEnvelope.Conditions[0].FieldName;
            problem.Extensions["fieldName"] = fieldName;
        }

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" },
        };
    }

    /// <summary>
    /// Extracts the single-quoted field name from a converter-emitted message like
    /// <c>"Field 'X' is not queryable for registry 'Solicitant'."</c>. Returns
    /// <see langword="null"/> when the pattern is not present.
    /// </summary>
    /// <param name="detail">Detail string from the failed Result.</param>
    /// <returns>The field name, or <see langword="null"/>.</returns>
    private static string? ExtractFieldName(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return null;
        }
        var firstQuote = detail.IndexOf('\'');
        if (firstQuote < 0)
        {
            return null;
        }
        var secondQuote = detail.IndexOf('\'', firstQuote + 1);
        if (secondQuote <= firstQuote)
        {
            return null;
        }
        return detail.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
    }

    /// <summary>
    /// Builds the 422 ProblemDetails for a too-broad query. Adds the verdict to the
    /// <c>extensions["budget"]</c> slot so the UI can render the refinement prompt
    /// without re-issuing the request.
    /// </summary>
    /// <param name="detail">Human-readable detail from the service failure.</param>
    /// <param name="verdict">
    /// The most recent budget verdict carried on the service instance. Defensive null:
    /// if the controller ever observes <see cref="ErrorCodes.QueryTooBroad"/> without
    /// a verdict, it still returns 422 with an empty <c>budget</c> extension instead
    /// of throwing — that branch indicates a service-side wiring bug worth fixing but
    /// not crashing the request over.
    /// </param>
    /// <returns>The 422 ProblemDetails action result.</returns>
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
    /// Translates a service-layer <see cref="QueryBudgetVerdict"/> to the wire DTO. A
    /// <c>null</c> verdict surfaces as a structurally-empty DTO so callers can rely on
    /// the shape of <c>extensions["budget"]</c>.
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
}
