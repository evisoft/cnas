using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Search;

/// <summary>
/// R0160 / R0161 / TOR CF 03.03 — cross-domain full-text-search surface. Returns a
/// merged + globally-ranked hit list across the five canonical domains
/// (applications, contributors, insured-persons, documents, dossiers) by running
/// per-domain queries in parallel and ordering the union by relevance rank.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate surface from <see cref="IFullTextSearchEngine"/>.</b> The
/// engine abstraction (R0522) returns ids from a single index — it is the
/// adapter slot that will swap to Elasticsearch when that operational batch
/// lands. The R0160 surface is the application-level orchestrator that fans out
/// across domains and merges results; it is the seam UI 003-007 binds to.
/// </para>
/// <para>
/// <b>Provider behaviour.</b> The production implementation
/// (<c>PostgresGlobalSearchService</c>) routes to <c>tsvector</c>-backed
/// generated columns + <c>ts_rank_cd</c> on the Npgsql provider, and falls
/// back to a substring-count rank on the EF Core InMemory provider so the test
/// suite remains deterministic without spinning a Postgres container.
/// </para>
/// </remarks>
public interface IGlobalSearchService
{
    /// <summary>
    /// Executes a cross-domain full-text search per the supplied input envelope.
    /// </summary>
    /// <param name="input">Search envelope (query + optional domain filter + paging).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success, the paged result list and the grand total. Failure codes:
    /// <see cref="ErrorCodes.ValidationFailed"/> when the envelope is malformed
    /// (empty query / out-of-range paging / unknown domain).
    /// </returns>
    Task<Result<GlobalSearchResultDto>> SearchAsync(
        GlobalSearchInputDto input,
        CancellationToken ct);
}
