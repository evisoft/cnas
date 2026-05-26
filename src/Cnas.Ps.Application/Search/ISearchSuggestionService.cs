using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Contracts.Search;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Search;

/// <summary>
/// R0525 / TOR CF 03.08 — produces structured refinement suggestions when a
/// list/search call returns a result set that exceeds the per-registry refinement
/// threshold. Distinct from <c>IQueryBudgetService</c> (R0167): the budget guard
/// REJECTS over-budget queries with a hard 422; this service emits ADVISORY
/// <see cref="SearchSuggestionDto"/> rows on a successful response so the UI can hint
/// at refinements without blocking the user.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threshold semantics.</b> Implementations are free to use a single global
/// threshold or per-registry thresholds; the default implementation
/// (<c>SearchSuggestionService</c>) uses a global 500-row threshold (the same magnitude
/// as the budget guard's tightest pre-configured registry policy).
/// </para>
/// <para>
/// <b>Discriminator-field heuristic.</b> The default rule reads the QBE schema for the
/// supplied registry and looks for a canonical "discriminator" field (e.g.
/// <c>IsActive</c> on <c>Solicitant</c>, <c>Status</c> on <c>Cerere</c>) — when the
/// caller's filter omits this field AND the row count is over the threshold, the
/// service emits a stable <see cref="SearchSuggestionDto"/> targeting that field. The
/// rule is intentionally narrow so the prompt feels relevant; future iterations may
/// add date-window heuristics.
/// </para>
/// <para>
/// <b>Stability of codes.</b> <see cref="SearchSuggestionDto.Code"/> and
/// <see cref="SearchSuggestionDto.ReasonCode"/> are part of the public API contract —
/// the catalogue grows additively. Renaming a code is a breaking change to the UI.
/// </para>
/// </remarks>
public interface ISearchSuggestionService
{
    /// <summary>
    /// Emits refinement suggestions for the supplied registry + filter + row-count
    /// triple. Returns an empty list when no suggestion applies (typical fast path —
    /// most calls stay under the threshold).
    /// </summary>
    /// <param name="registry">
    /// Stable registry code (e.g. <c>"Solicitant"</c>, <c>"Cerere"</c>). Must match
    /// the registry the caller's filter was authored against.
    /// </param>
    /// <param name="currentFilter">
    /// QBE envelope the caller supplied. Used to decide whether the discriminator
    /// field is already in play; null is treated as the empty filter.
    /// </param>
    /// <param name="currentRowCount">
    /// Row count produced by the caller's filter on the same call. Implementations
    /// compare this against the registry's refinement threshold.
    /// </param>
    /// <param name="ct">Cancellation token. Implementations stay synchronous in practice but the contract is async-shaped to permit future I/O lookups.</param>
    /// <returns>
    /// On success, the (possibly empty) suggestion list. Failure is reserved for
    /// future expansion (e.g. <see cref="ErrorCodes.QbeRegistryUnknown"/> when the
    /// registry name is unknown).
    /// </returns>
    Task<Result<IReadOnlyList<SearchSuggestionDto>>> SuggestRefinementsAsync(
        string registry,
        QbeFilter? currentFilter,
        int currentRowCount,
        CancellationToken ct);
}
