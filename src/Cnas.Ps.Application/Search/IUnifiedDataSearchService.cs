using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Search;

/// <summary>
/// R0520 / TOR CF 03.01 — input envelope for the unified cross-entity search
/// surface (<see cref="IUnifiedDataSearchService"/>). Carries the free-text
/// query, an optional per-domain filter (across all nine canonical unified
/// domains), and paging.
/// </summary>
/// <remarks>
/// Kept separate from <c>GlobalSearchInputDto</c> so the v1 (five-domain)
/// surface remains backward-compatible; the unified envelope adds the four
/// extra domain codes from <see cref="GlobalSearchDomains.Unified"/>.
/// </remarks>
/// <param name="Query">Free-text query (1..256 chars).</param>
/// <param name="Domains">
/// Optional list of canonical unified domain codes. Null / empty = fan out to
/// every domain in <see cref="GlobalSearchDomains.Unified"/>.
/// </param>
/// <param name="Skip">Zero-based skip (paging). Must be ≥ 0.</param>
/// <param name="Take">Page size. Must be 1..100; the service hard-caps at 100.</param>
public sealed record UnifiedSearchInput(
    string Query,
    IReadOnlyList<string>? Domains,
    int Skip,
    int Take);

/// <summary>
/// R0520 / TOR CF 03.01 — wire response for the unified search surface.
/// Carries the merged + globally-ranked hit list, the grand total, and the
/// echoed paging.
/// </summary>
/// <param name="TotalHits">Total hits across every queried domain BEFORE paging.</param>
/// <param name="Results">Paged hit list, sorted by descending relevance score.</param>
/// <param name="Skip">Echoed skip count.</param>
/// <param name="Take">Echoed take count.</param>
public sealed record UnifiedSearchResult(
    int TotalHits,
    IReadOnlyList<UnifiedSearchHitDto> Results,
    int Skip,
    int Take);

/// <summary>
/// R0520 / TOR CF 03.01 — unified cross-entity search surface. Returns a
/// merged + globally-ranked hit list across the nine canonical domains
/// (applicants, applications, dossiers, payers, insured persons, tasks,
/// notifications, issued documents, workflow documents), with every hit
/// projected into the same <see cref="UnifiedSearchHitDto"/> shape so the UI
/// can render every row with a single template.
/// </summary>
/// <remarks>
/// Each per-domain projector applies the row-level scope from
/// <see cref="ISearchRowLevelFilter"/> so a non-super-role caller only sees
/// rows their ABAC scope permits (R0526 / CF 03.10).
/// </remarks>
public interface IUnifiedDataSearchService
{
    /// <summary>
    /// Executes a unified cross-domain search per the supplied input envelope.
    /// The <paramref name="user"/> drives the row-level ABAC scope.
    /// </summary>
    /// <param name="input">Search envelope.</param>
    /// <param name="user">The calling principal whose roles + claims drive the row-level scope.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The paged result on success. Failure code:
    /// <see cref="ErrorCodes.ValidationFailed"/> on a malformed envelope.
    /// </returns>
    Task<Result<UnifiedSearchResult>> SearchAsync(
        UnifiedSearchInput input,
        ClaimsPrincipal user,
        CancellationToken ct);
}
