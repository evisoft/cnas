namespace Cnas.Ps.Contracts;

/// <summary>
/// R0167 / TOR CF 01.06 / CF 03.07-08 — outbound DTO mirroring the service-layer
/// <c>QueryBudgetVerdict</c>. Carried on the ProblemDetails returned by a list endpoint
/// that refused to materialise a too-broad query, so the UI can render a structured
/// refinement prompt instead of a generic error.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sqid invariant.</b> This DTO MUST NOT carry any raw database identifiers
/// (CLAUDE.md RULE 3). The verdict describes the SHAPE of a query, not its rows.
/// <see cref="Registry"/> is a stable string constant (see <c>QueryBudgetRegistries</c>),
/// not an opaque id.
/// </para>
/// <para>
/// <b>Stability.</b> The hint codes (<see cref="QueryBudgetRefinementHintDto.Reason"/>)
/// are part of the public API contract — the UI binds localisation strings to them.
/// Adding new codes is additive; renaming an existing code is a breaking change.
/// </para>
/// </remarks>
/// <param name="Registry">
/// Stable registry code the verdict describes (e.g. <c>"Solicitant"</c>). One of the
/// constants exposed by the server-side <c>QueryBudgetRegistries</c> class.
/// </param>
/// <param name="EstimatedRowCount">
/// Server's estimate of the row count the unfiltered (or under-filtered) query would
/// have produced. May be <see cref="int.MaxValue"/> when the count itself exceeded the
/// 5-second evaluation budget — the count was aborted because there is no point
/// counting longer than the threshold.
/// </param>
/// <param name="Budget">
/// The numeric row budget that was applied. The verdict's <c>Allowed</c> flag is
/// effectively <c>EstimatedRowCount &lt;= Budget</c>.
/// </param>
/// <param name="Hints">
/// Ordered list of refinement suggestions. Required hints come first, Suggested hints
/// follow. Empty when the query fit the budget (no ProblemDetails would have been
/// emitted in that case).
/// </param>
public sealed record QueryBudgetVerdictDto(
    string Registry,
    int EstimatedRowCount,
    int Budget,
    IReadOnlyList<QueryBudgetRefinementHintDto> Hints);

/// <summary>
/// R0167 — one entry in <see cref="QueryBudgetVerdictDto.Hints"/>. Each entry tells the
/// UI which filter field to nudge the caller toward, with a stable string code the UI
/// can resolve to a localised message.
/// </summary>
/// <param name="FieldName">
/// Canonical filter field name (e.g. <c>"Q"</c>, <c>"CreatedFromUtc"</c>,
/// <c>"Status"</c>). Stable identifier — must match the input DTO field the caller
/// would set to satisfy the hint.
/// </param>
/// <param name="Severity">
/// <c>"Required"</c> when the registry will refuse any query that omits this field;
/// <c>"Suggested"</c> when adding the field would help but is not strictly necessary.
/// Required hints are rendered first in <see cref="QueryBudgetVerdictDto.Hints"/>.
/// </param>
/// <param name="Reason">
/// Stable string code (e.g. <c>"AddDateFilter"</c>, <c>"AddStatusFilter"</c>,
/// <c>"AddFreeTextFilter"</c>) that the UI maps to a localised explanatory message.
/// </param>
public sealed record QueryBudgetRefinementHintDto(
    string FieldName,
    string Severity,
    string Reason);
