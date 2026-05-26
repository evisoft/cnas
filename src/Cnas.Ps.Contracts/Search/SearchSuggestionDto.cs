namespace Cnas.Ps.Contracts.Search;

/// <summary>
/// R0525 / TOR CF 03.08 — structured refinement suggestion emitted by
/// <c>Cnas.Ps.Application.Search.ISearchSuggestionService</c> when a list/search call
/// produces a row count that exceeds the per-registry refinement threshold. Distinct
/// from the existing <see cref="QueryBudgetRefinementHintDto"/> (R0167) which is
/// scoped to the strict budget-rejection 422 path — suggestions are advisory and ride
/// alongside a successful 200 response.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stability.</b> <see cref="Code"/> and <see cref="ReasonCode"/> are part of the
/// public API contract — UI surfaces branch on the literal values to render localised
/// prompts (e.g. <c>"AddStatusFilter"</c> renders as "Add a status filter to narrow
/// the list"). Renaming is a breaking change; the catalogue grows additively.
/// </para>
/// </remarks>
/// <param name="Code">
/// Stable suggestion code. Catalogue (additive): <c>"AddStatusFilter"</c>,
/// <c>"AddDateFilter"</c>, <c>"AddFreeTextFilter"</c>, <c>"NarrowDateWindow"</c>.
/// </param>
/// <param name="FieldName">
/// Canonical field name the suggestion targets — taken from the registry's QBE schema
/// so the UI can correlate the prompt to a specific QBE form row.
/// </param>
/// <param name="ReasonCode">
/// Stable machine-readable reason. Catalogue: <c>"TooBroad"</c> (result set above the
/// refinement threshold), <c>"AmbiguousMatch"</c> (heuristic placeholder for future
/// "did you mean…" rules).
/// </param>
/// <param name="ExampleValue">
/// Optional hint value the UI can pre-fill into the suggested filter row. Null when no
/// concrete example applies (e.g. for free-text filters).
/// </param>
public sealed record SearchSuggestionDto(
    string Code,
    string FieldName,
    string ReasonCode,
    string? ExampleValue = null);

/// <summary>
/// R0522 / TOR CF 03.03 — wire DTO returned by <see cref="object"/>-typed full-text
/// search engines. Carries the matching ID list (Sqid-encoded per CLAUDE.md RULE 3)
/// plus a total-row count so the caller can drive paging without a second round trip
/// to the engine.
/// </summary>
/// <param name="Ids">Sqid-encoded ids of the matching rows, in the engine's relevance order.</param>
/// <param name="TotalCount">Total matches the engine could surface for the query.</param>
public sealed record FullTextSearchResultDto(
    IReadOnlyList<string> Ids,
    int TotalCount);
