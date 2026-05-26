using System.Linq;

namespace Cnas.Ps.Application.QueryBudget;

/// <summary>
/// R0167 / TOR CF 01.06 / CF 03.07-08 — service contract for evaluating whether a
/// filtered <see cref="IQueryable"/> would breach the configured per-registry row
/// budget. A list-endpoint service call wraps the filter-applied query (pre-Skip /
/// pre-Take) and consults this service before materialising; if the verdict is
/// <see cref="QueryBudgetVerdict.Allowed"/> = <c>false</c> the call returns a
/// <c>QUERY_TOO_BROAD</c> failure carrying the verdict for the controller to surface
/// as a 422 ProblemDetails.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a count-first guard.</b> A naive list endpoint that materialises 8 000 rows
/// before clamping to a single page causes 10× the DB and memory pressure of the
/// answer the UI actually needs. <see cref="EvaluateAsync"/> issues a single
/// <c>SELECT COUNT(*)</c> over the SAME filtered queryable so the cost is bounded by
/// the database's ability to count, not by the result size.
/// </para>
/// <para>
/// <b>Self-throttling counter.</b> Implementations bound the COUNT call itself to a
/// fixed wall-clock budget (5 s) — if the count is slow enough to risk a hung worker
/// thread, the verdict is <see cref="QueryBudgetVerdict.Allowed"/> = <c>false</c>
/// with <see cref="QueryBudgetVerdict.EstimatedRowCount"/> = <see cref="int.MaxValue"/>.
/// The point of the refinement prompt is to drive the caller to add filters; "the
/// COUNT timed out" is itself a signal that the registry needs filters before the
/// query is healthy.
/// </para>
/// <para>
/// <b>Cancellation invariant.</b> <see cref="EvaluateAsync"/> respects the supplied
/// <see cref="CancellationToken"/>. A pre-cancelled token throws
/// <see cref="OperationCanceledException"/> directly — the budget guard does NOT
/// swallow caller cancellation.
/// </para>
/// </remarks>
public interface IQueryBudgetService
{
    /// <summary>
    /// Counts rows of <paramref name="countableQuery"/> and compares against the
    /// budget configured for <paramref name="registry"/>. Emits one observability
    /// counter per call (<c>cnas.query.budget_evaluated</c>) plus a second counter
    /// (<c>cnas.query.budget_rejected</c>) when the verdict refuses the query.
    /// </summary>
    /// <param name="registry">Stable registry code (see <see cref="QueryBudgetRegistries"/>).</param>
    /// <param name="countableQuery">
    /// Filter-applied queryable BEFORE the call-site applies Skip / Take. The service
    /// issues <c>LongCountAsync</c> against this queryable — providers that don't
    /// translate <c>LongCountAsync</c> aren't supported.
    /// </param>
    /// <param name="context">
    /// Caller-supplied filter envelope; the policy resolver inspects it to decide
    /// which hints to fire. Pass an empty <see cref="QueryFilterContext"/> when the
    /// caller supplied no filters.
    /// </param>
    /// <param name="ct">Cancellation token; honoured throughout the count.</param>
    /// <returns>The verdict, with hints populated when the budget is exceeded.</returns>
    Task<QueryBudgetVerdict> EvaluateAsync(
        string registry,
        IQueryable countableQuery,
        IQueryFilterContext context,
        CancellationToken ct = default);
}

/// <summary>
/// R0167 — outcome record returned by <see cref="IQueryBudgetService.EvaluateAsync"/>.
/// Service-layer code surfaces it through a <c>Result&lt;T&gt;.Failure(...,
/// "QUERY_TOO_BROAD")</c> envelope (paired with <see cref="QueryBudgetFailureEnvelope"/>)
/// when <see cref="Allowed"/> is <c>false</c>.
/// </summary>
/// <param name="Allowed">
/// <c>true</c> when the row count fit within the budget; <c>false</c> otherwise.
/// </param>
/// <param name="EstimatedRowCount">
/// Number of rows the filtered query would have produced. Clamped to <see cref="int"/>
/// because the budget is an <see cref="int"/> too — callers don't need more precision
/// than that. May be <see cref="int.MaxValue"/> when the count itself timed out.
/// </param>
/// <param name="Budget">The budget that was applied (per the resolved policy).</param>
/// <param name="Registry">
/// Stable registry code echoed back; identical to the input so callers can use the
/// verdict as a self-contained record without retaining the input separately.
/// </param>
/// <param name="Hints">
/// Refinement suggestions in the policy's standard order (Required-first, then
/// Suggested). Empty when <see cref="Allowed"/> is <c>true</c>.
/// </param>
public sealed record QueryBudgetVerdict(
    bool Allowed,
    int EstimatedRowCount,
    int Budget,
    string Registry,
    IReadOnlyList<RefinementHint> Hints);

/// <summary>
/// R0167 — typed envelope plumbed through the service-layer <c>Result&lt;T&gt;</c>
/// failure so the controller can recover the verdict that drove the
/// <c>QUERY_TOO_BROAD</c> rejection. The verdict is not part of the generic
/// <c>Result</c> struct because the struct intentionally has no payload bag — this
/// envelope IS the payload bag, scoped to this failure mode.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lookup pattern.</b> The service-method local that drives the rejection caches
/// the most recently produced verdict on its own field/property and exposes it via a
/// service-level method (e.g. <c>ISolicitantService.LastQueryBudgetVerdict</c>) so the
/// controller can correlate the failure with the verdict. For services that are
/// stateless this pattern still works because each service instance is per-request
/// (Scoped lifetime).
/// </para>
/// <para>
/// <b>Stable error code.</b> Use <c>ErrorCodes.QueryTooBroad</c>
/// (<c>"QUERY_TOO_BROAD"</c>) for the <c>Result.Failure</c> code so controllers can
/// branch on it deterministically.
/// </para>
/// </remarks>
public static class QueryBudgetFailureEnvelope
{
    /// <summary>Human-readable message attached to the failure result.</summary>
    public const string FailureMessage = "The query would exceed the configured row budget. Narrow the filter and retry.";
}
