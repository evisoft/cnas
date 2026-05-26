using System.Diagnostics;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.QueryBudget;

/// <summary>
/// R0167 / TOR CF 01.06 / CF 03.07-08 — production <see cref="IQueryBudgetService"/>
/// implementation. Counts the supplied <see cref="IQueryable"/> via
/// <see cref="EntityFrameworkQueryableExtensions.LongCountAsync{TSource}(IQueryable{TSource}, CancellationToken)"/>
/// (translated to <c>SELECT COUNT(*)</c> on relational providers), aborts the count
/// after a wall-clock budget, and assembles a <see cref="QueryBudgetVerdict"/> with
/// refinement hints driven by the resolved policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>5-second hard cap.</b> The count itself is bounded by a linked
/// <see cref="CancellationTokenSource"/> that fires after
/// <see cref="CountTimeoutSeconds"/>. A timed-out count returns a refusing verdict
/// with <see cref="QueryBudgetVerdict.EstimatedRowCount"/> = <see cref="int.MaxValue"/>
/// rather than re-throwing — the refinement-prompt UX is the same whether the count
/// finished or aborted. The caller's own cancellation token still takes precedence;
/// passing an already-cancelled token throws <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// <b>Reflection-free hint emission.</b> The policy's <see cref="RefinementHintRule"/>
/// predicates run against the supplied <see cref="IQueryFilterContext"/>; the service
/// does not introspect the original input DTO. Required-severity hints are emitted
/// before Suggested ones to satisfy the ordering invariant documented on
/// <see cref="IQueryBudgetPolicy"/>.
/// </para>
/// <para>
/// <b>Thread safety.</b> Stateless — safe to register as a Singleton in DI. (Currently
/// registered Scoped to mirror the rest of the per-request service surface; that is
/// also fine.)
/// </para>
/// </remarks>
public sealed class QueryBudgetService : IQueryBudgetService
{
    /// <summary>Hard upper bound on the count call's wall-clock time, in seconds.</summary>
    public const int CountTimeoutSeconds = 5;

    /// <summary>Resolver that maps registry → policy.</summary>
    private readonly IQueryBudgetPolicy _policy;

    /// <summary>Diagnostic sink for slow / aborted counts.</summary>
    private readonly ILogger<QueryBudgetService> _logger;

    /// <summary>Constructs the service with its dependencies.</summary>
    /// <param name="policy">Per-registry policy resolver.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public QueryBudgetService(IQueryBudgetPolicy policy, ILogger<QueryBudgetService> logger)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(logger);
        _policy = policy;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<QueryBudgetVerdict> EvaluateAsync(
        string registry,
        IQueryable countableQuery,
        IQueryFilterContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registry);
        ArgumentNullException.ThrowIfNull(countableQuery);
        ArgumentNullException.ThrowIfNull(context);

        // Honour caller cancellation BEFORE any work — a pre-cancelled token must throw
        // promptly so the caller's "I changed my mind" semantics are preserved.
        ct.ThrowIfCancellationRequested();

        var policy = _policy.GetForRegistry(registry);
        var rowCount = await TryCountAsync(countableQuery, ct).ConfigureAwait(false);
        var clamped = rowCount > int.MaxValue ? int.MaxValue : (int)rowCount;
        var allowed = clamped <= policy.Budget;

        var hints = allowed
            ? Array.Empty<RefinementHint>()
            : BuildHints(policy, context);

        // Per-call counter, tagged with the registry and outcome. Bounded cardinality:
        // 6 registries × 2 outcomes = 12 distinct tag tuples.
        CnasMeter.QueryBudgetEvaluated.Add(
            1,
            new KeyValuePair<string, object?>("registry", policy.Registry),
            new KeyValuePair<string, object?>("allowed", allowed));

        if (!allowed)
        {
            CnasMeter.QueryBudgetRejected.Add(
                1,
                new KeyValuePair<string, object?>("registry", policy.Registry));
        }

        return new QueryBudgetVerdict(allowed, clamped, policy.Budget, policy.Registry, hints);
    }

    /// <summary>
    /// Counts the queryable with a 5-second wall-clock cap. Returns
    /// <see cref="long.MaxValue"/> when the cap fires (the verdict surfaces this as
    /// <see cref="int.MaxValue"/> to the caller).
    /// </summary>
    /// <remarks>
    /// The implementation links the caller's cancellation token with a fresh
    /// <see cref="CancellationTokenSource"/> set to <see cref="CountTimeoutSeconds"/>.
    /// When the timeout source cancels, the resulting
    /// <see cref="OperationCanceledException"/> originates from the LINKED token; we
    /// re-throw only when the CALLER's token was the one cancelled — otherwise we
    /// swallow and return <see cref="long.MaxValue"/>.
    /// </remarks>
    /// <param name="query">Queryable to count.</param>
    /// <param name="callerToken">Caller's cancellation token; always honoured.</param>
    /// <returns>The counted rows, or <see cref="long.MaxValue"/> on timeout.</returns>
    private async Task<long> TryCountAsync(IQueryable query, CancellationToken callerToken)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(CountTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, timeoutCts.Token);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // EF Core's LongCountAsync extension method lives on IQueryable<T>; we have
            // IQueryable (non-generic) so we go through the reflection-friendly entry
            // point exposed by Queryable.LongCount — but that synchronous version would
            // block the worker thread. Instead we forward to EF's async machinery via
            // EntityFrameworkQueryableExtensions.LongCountAsync after a runtime cast.
            return await CountAsyncCore(query, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !callerToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Query budget count timed out after {ElapsedMs} ms (cap = {CapSeconds}s).",
                stopwatch.ElapsedMilliseconds,
                CountTimeoutSeconds);
            return long.MaxValue;
        }
    }

    /// <summary>
    /// Forwards to <see cref="EntityFrameworkQueryableExtensions.LongCountAsync{TSource}(IQueryable{TSource}, CancellationToken)"/>
    /// via a single-arg generic cast. The non-generic <see cref="IQueryable"/> we receive
    /// from the service-layer call site is always a <see cref="IQueryable{T}"/> at
    /// runtime (constructed via <c>db.Solicitants.Where(...)</c>), so the cast is safe;
    /// we use reflection to recover the element type so the extension call binds.
    /// </summary>
    /// <param name="query">The queryable to count.</param>
    /// <param name="ct">Linked cancellation token (timeout + caller).</param>
    /// <returns>The row count.</returns>
    private static Task<long> CountAsyncCore(IQueryable query, CancellationToken ct)
    {
        var elementType = query.ElementType;
        // Resolve EF Core's generic LongCountAsync<TSource>(IQueryable<TSource>, CancellationToken)
        // at runtime. Cached lookup is overkill here — the budget service is called at
        // most once per list request and the reflection cost is dwarfed by the SQL
        // round-trip.
        var methodInfo = typeof(EntityFrameworkQueryableExtensions)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .First(m =>
                m.Name == nameof(EntityFrameworkQueryableExtensions.LongCountAsync)
                && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 2);
        var closed = methodInfo.MakeGenericMethod(elementType);
        var task = (Task<long>)closed.Invoke(null, new object[] { query, ct })!;
        return task;
    }

    /// <summary>
    /// Materialises the refinement hints for a too-broad query. Iterates the policy's
    /// rule list, calls each predicate against <paramref name="context"/>, and emits
    /// hints Required-first then Suggested-second.
    /// </summary>
    /// <param name="policy">Resolved policy.</param>
    /// <param name="context">Caller filter context.</param>
    /// <returns>Ordered hint list (may be empty when no rules fired).</returns>
    private static IReadOnlyList<RefinementHint> BuildHints(QueryBudgetPolicy policy, IQueryFilterContext context)
    {
        if (policy.Rules.Count == 0)
        {
            return Array.Empty<RefinementHint>();
        }
        var required = new List<RefinementHint>();
        var suggested = new List<RefinementHint>();
        foreach (var rule in policy.Rules)
        {
            if (!rule.AppliesWhen(context))
            {
                continue;
            }
            var hint = new RefinementHint(rule.FieldName, rule.Severity, rule.Reason);
            if (string.Equals(rule.Severity, RefinementHintSeverity.Required, StringComparison.Ordinal))
            {
                required.Add(hint);
            }
            else
            {
                suggested.Add(hint);
            }
        }
        if (required.Count == 0 && suggested.Count == 0)
        {
            return Array.Empty<RefinementHint>();
        }
        // Concatenate Required-first then Suggested.
        required.AddRange(suggested);
        return required;
    }
}
