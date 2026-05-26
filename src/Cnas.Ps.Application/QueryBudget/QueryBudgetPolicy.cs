namespace Cnas.Ps.Application.QueryBudget;

/// <summary>
/// R0167 — severity codes for <see cref="RefinementHint"/> ordering and UI rendering.
/// Stable string constants so the UI can branch on them without hard-coding case-
/// sensitive comparisons of free-text labels.
/// </summary>
public static class RefinementHintSeverity
{
    /// <summary>
    /// The registry will refuse any query that omits this field. Rendered first in
    /// hint lists; the UI should mark these fields as mandatory in the refinement
    /// prompt.
    /// </summary>
    public const string Required = "Required";

    /// <summary>
    /// Adding this field would narrow the result set but is not strictly necessary.
    /// Rendered after <see cref="Required"/> hints; the UI may render these as
    /// optional suggestions.
    /// </summary>
    public const string Suggested = "Suggested";
}

/// <summary>
/// R0167 — stable string codes attached to <see cref="RefinementHint.Reason"/>. The UI
/// resolves each code to a localised explanatory message ("add a date filter to narrow
/// the search", ...). Adding new codes is additive; renaming existing ones is a
/// breaking API change.
/// </summary>
public static class RefinementHintReasons
{
    /// <summary>Caller should supply a free-text query term (e.g. name or IDNP fragment).</summary>
    public const string AddFreeTextFilter = "AddFreeTextFilter";

    /// <summary>Caller should supply a date range to bound the result set in time.</summary>
    public const string AddDateFilter = "AddDateFilter";

    /// <summary>Caller should narrow by status to exclude the long-tail of completed rows.</summary>
    public const string AddStatusFilter = "AddStatusFilter";

    /// <summary>Caller should pin to an owner / assignee user when triaging a single inbox.</summary>
    public const string AddOwnerFilter = "AddOwnerFilter";

    /// <summary>Caller should provide an identifier-style filter (Sqid or IDNP).</summary>
    public const string AddIdentifierFilter = "AddIdentifierFilter";
}

/// <summary>
/// R0167 — a single refinement suggestion attached to a too-broad-query verdict. Built
/// by the policy resolver from the policy's <see cref="RefinementHintRule"/> set.
/// </summary>
/// <param name="FieldName">
/// Canonical input-DTO field name (PascalCase) the caller should set to satisfy the
/// hint. Same string the UI binds to its corresponding form field.
/// </param>
/// <param name="Severity">
/// One of <see cref="RefinementHintSeverity.Required"/> /
/// <see cref="RefinementHintSeverity.Suggested"/>.
/// </param>
/// <param name="Reason">Stable code from <see cref="RefinementHintReasons"/>.</param>
public sealed record RefinementHint(string FieldName, string Severity, string Reason);

/// <summary>
/// R0167 — declarative rule that decides whether a particular refinement hint should
/// fire for the caller's filter context. Stored on <see cref="QueryBudgetPolicy"/> and
/// evaluated by <see cref="IQueryBudgetPolicy"/> when a query exceeds its budget.
/// </summary>
/// <param name="FieldName">Field the hint nudges the caller toward (matches input-DTO PascalCase).</param>
/// <param name="Severity">
/// <see cref="RefinementHintSeverity.Required"/> or <see cref="RefinementHintSeverity.Suggested"/>.
/// </param>
/// <param name="Reason">Stable code from <see cref="RefinementHintReasons"/>.</param>
/// <param name="AppliesWhen">
/// Predicate returning <c>true</c> when the hint should fire. By convention the
/// predicate inspects the filter context with <see cref="IQueryFilterContext.Has"/>
/// and returns <c>true</c> when the field is missing (so the hint suggests adding it).
/// </param>
public sealed record RefinementHintRule(
    string FieldName,
    string Severity,
    string Reason,
    Func<IQueryFilterContext, bool> AppliesWhen);

/// <summary>
/// R0167 — declarative per-registry policy describing the row-count budget and the
/// set of refinement-hint rules to evaluate when the budget is exceeded.
/// </summary>
/// <remarks>
/// The policy is data-driven so adding a new registry needs only a single fluent
/// builder declaration in startup configuration — neither the budget service nor the
/// policy resolver carry per-registry switches.
/// </remarks>
/// <param name="Registry">Stable registry code from <see cref="QueryBudgetRegistries"/>.</param>
/// <param name="Budget">
/// Maximum number of rows the registry will materialise for a single list query.
/// Defaults to 5000 (UI 014 / CF 01.06 — paging UI cannot tolerate larger sets).
/// Registries holding heavier rows (audit logs) override down to 1000.
/// </param>
/// <param name="Rules">
/// Ordered hint rules. The policy resolver returns hints in the order
/// (<see cref="RefinementHintSeverity.Required"/> first, then
/// <see cref="RefinementHintSeverity.Suggested"/>) — see <see cref="IQueryBudgetPolicy"/>
/// remarks.
/// </param>
public sealed record QueryBudgetPolicy(
    string Registry,
    int Budget,
    IReadOnlyList<RefinementHintRule> Rules)
{
    /// <summary>
    /// Default per-registry budget when a policy doesn't override it. 5000 rows mirrors
    /// the upper bound the paging UI can render without freezing the browser
    /// (CF 01.06 / UI 014).
    /// </summary>
    public const int DefaultBudget = 5000;
}

/// <summary>
/// R0167 — per-registry resolver for <see cref="QueryBudgetPolicy"/> records. The
/// concrete implementation is registered as a singleton in DI and seeded at startup
/// from <see cref="QueryBudgetPolicyBuilder"/> declarations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unknown registries.</b> Implementations MUST NOT throw on an unknown registry —
/// instead they return a default <see cref="QueryBudgetPolicy"/> (budget
/// <see cref="QueryBudgetPolicy.DefaultBudget"/>, zero rules) so a new list endpoint
/// added before its policy entry still benefits from the budget guard. The
/// implementation SHOULD log a warning so operators notice the missing registration.
/// </para>
/// <para>
/// <b>Hint ordering invariant.</b> Consumers may rely on hints emerging
/// Required-first then Suggested-second — the policy resolver guarantees this even if
/// the underlying rule list was declared out of order.
/// </para>
/// </remarks>
public interface IQueryBudgetPolicy
{
    /// <summary>
    /// Resolves the policy for <paramref name="registry"/>. Returns a default policy
    /// (budget 5000, no rules) for unknown registries.
    /// </summary>
    /// <param name="registry">Stable registry code; nullable / unknown returns the default.</param>
    /// <returns>The matching policy or a default placeholder.</returns>
    QueryBudgetPolicy GetForRegistry(string registry);
}

/// <summary>
/// R0167 — fluent builder that produces a single <see cref="QueryBudgetPolicy"/>. Used
/// by the Infrastructure-layer static registry to declare each registry's budget and
/// hints in one expression.
/// </summary>
/// <remarks>
/// Builders are MUTABLE accumulators; <see cref="Build"/> takes a snapshot. Re-using a
/// builder after <see cref="Build"/> is allowed (the snapshot is independent) but most
/// callers discard the builder after a single Build.
/// </remarks>
public sealed class QueryBudgetPolicyBuilder
{
    /// <summary>Stable registry code (e.g. <c>"Solicitant"</c>).</summary>
    private readonly string _registry;

    /// <summary>Mutable accumulator for hint rules; flushed to a read-only list in <see cref="Build"/>.</summary>
    private readonly List<RefinementHintRule> _rules = [];

    /// <summary>Mutable budget; defaults to <see cref="QueryBudgetPolicy.DefaultBudget"/>.</summary>
    private int _budget = QueryBudgetPolicy.DefaultBudget;

    /// <summary>Private constructor — call <see cref="For(string)"/>.</summary>
    /// <param name="registry">Stable registry code.</param>
    private QueryBudgetPolicyBuilder(string registry)
    {
        _registry = registry;
    }

    /// <summary>Starts a new builder for the named <paramref name="registry"/>.</summary>
    /// <param name="registry">Stable registry code from <see cref="QueryBudgetRegistries"/>.</param>
    /// <returns>A fresh builder ready for chaining.</returns>
    public static QueryBudgetPolicyBuilder For(string registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registry);
        return new QueryBudgetPolicyBuilder(registry);
    }

    /// <summary>
    /// Sets the row budget. Defaults to <see cref="QueryBudgetPolicy.DefaultBudget"/>
    /// when not called.
    /// </summary>
    /// <param name="budget">Positive row count; values &lt;= 0 are rejected.</param>
    /// <returns>This builder, for chaining.</returns>
    public QueryBudgetPolicyBuilder WithBudget(int budget)
    {
        if (budget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), budget, "Budget must be positive.");
        }
        _budget = budget;
        return this;
    }

    /// <summary>
    /// Declares a REQUIRED hint that fires when the caller did not supply
    /// <paramref name="fieldName"/>. Equivalent to <see cref="RequireWhen(string, string, Func{IQueryFilterContext, bool})"/>
    /// with the default "field missing" predicate.
    /// </summary>
    /// <param name="fieldName">PascalCase input-DTO field name.</param>
    /// <param name="reason">Stable code from <see cref="RefinementHintReasons"/>.</param>
    /// <returns>This builder, for chaining.</returns>
    public QueryBudgetPolicyBuilder Require(string fieldName, string reason = RefinementHintReasons.AddFreeTextFilter) =>
        RequireWhen(fieldName, reason, ctx => !ctx.Has(fieldName));

    /// <summary>
    /// Declares a REQUIRED hint with a custom predicate. Use this when the "missing"
    /// condition needs to look at multiple fields (e.g. "Status OR DateRange must be
    /// present").
    /// </summary>
    /// <param name="fieldName">PascalCase input-DTO field name (the field the hint NUDGES toward).</param>
    /// <param name="reason">Stable code from <see cref="RefinementHintReasons"/>.</param>
    /// <param name="appliesWhen">Predicate; return <c>true</c> when the hint should fire.</param>
    /// <returns>This builder, for chaining.</returns>
    public QueryBudgetPolicyBuilder RequireWhen(string fieldName, string reason, Func<IQueryFilterContext, bool> appliesWhen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(appliesWhen);
        _rules.Add(new RefinementHintRule(fieldName, RefinementHintSeverity.Required, reason, appliesWhen));
        return this;
    }

    /// <summary>
    /// Declares a SUGGESTED hint that fires when the caller did not supply
    /// <paramref name="fieldName"/>. Equivalent to
    /// <see cref="SuggestWhen(string, string, Func{IQueryFilterContext, bool})"/> with
    /// the default "field missing" predicate.
    /// </summary>
    /// <param name="fieldName">PascalCase input-DTO field name.</param>
    /// <param name="reason">Stable code from <see cref="RefinementHintReasons"/>.</param>
    /// <returns>This builder, for chaining.</returns>
    public QueryBudgetPolicyBuilder Suggest(string fieldName, string reason = RefinementHintReasons.AddDateFilter) =>
        SuggestWhen(fieldName, reason, ctx => !ctx.Has(fieldName));

    /// <summary>
    /// Declares a SUGGESTED hint with a custom predicate.
    /// </summary>
    /// <param name="fieldName">PascalCase input-DTO field name (the field the hint NUDGES toward).</param>
    /// <param name="reason">Stable code from <see cref="RefinementHintReasons"/>.</param>
    /// <param name="appliesWhen">Predicate; return <c>true</c> when the hint should fire.</param>
    /// <returns>This builder, for chaining.</returns>
    public QueryBudgetPolicyBuilder SuggestWhen(string fieldName, string reason, Func<IQueryFilterContext, bool> appliesWhen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(appliesWhen);
        _rules.Add(new RefinementHintRule(fieldName, RefinementHintSeverity.Suggested, reason, appliesWhen));
        return this;
    }

    /// <summary>
    /// Materialises the policy. Rules are emitted in declaration order; the policy
    /// resolver re-orders them Required-first when presenting hints to callers.
    /// </summary>
    /// <returns>An immutable <see cref="QueryBudgetPolicy"/> snapshot.</returns>
    public QueryBudgetPolicy Build() => new(_registry, _budget, _rules.ToArray());
}
