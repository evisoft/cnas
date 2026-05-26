using Cnas.Ps.Contracts.Search;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Search;

/// <summary>
/// R0522 / TOR CF 03.03 — abstraction over a full-text search engine. The codebase
/// ships a Postgres ILIKE-backed default (<c>PostgresIlikeFullTextSearchEngine</c>) and
/// a <c>NotImplementedExternalSearchEngine</c> placeholder that documents the gap to a
/// real Elasticsearch/Solr deployment without committing to one in this batch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an abstraction with no real engine.</b> The TOR mandates a "specialized
/// search engine with morphological matching" (CF 03.03). The infrastructure piece
/// (deploying and operating Elasticsearch / Solr) is a separate batch on the
/// operational backlog; the application-layer code that consumes such an engine
/// (cross-registry search, suggest-as-you-type) needs the abstraction now so a future
/// infra swap does not ripple through service code.
/// </para>
/// <para>
/// <b>Index name semantics.</b> <see cref="SearchAsync"/> takes an <c>indexName</c> —
/// the Postgres adapter maps this to a registry-specific projection (e.g.
/// <c>"Solicitant"</c> -> <c>DbSet&lt;Solicitant&gt;.Where(s =&gt; ILike(name, q))</c>);
/// the future Elasticsearch adapter will map it to the actual ES index alias.
/// </para>
/// </remarks>
public interface IFullTextSearchEngine
{
    /// <summary>
    /// Stable engine name, used by observability + diagnostics endpoints so operators
    /// can see which adapter is wired ("PostgresIlike", "Elasticsearch",
    /// "NotImplementedExternal").
    /// </summary>
    string EngineName { get; }

    /// <summary>
    /// Executes a full-text query against the supplied index/registry and returns the
    /// matching ids (Sqid-encoded per CLAUDE.md RULE 3) plus a total-count.
    /// </summary>
    /// <param name="indexName">
    /// Stable index identifier. The Postgres adapter accepts a registry code
    /// (<c>"Solicitant"</c> today; extensible to other registries by adding cases to
    /// its dispatch).
    /// </param>
    /// <param name="query">User-entered free-text query; trimmed by the implementation.</param>
    /// <param name="skip">Number of rows to skip (paging).</param>
    /// <param name="take">Max rows to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success, the matching id list + total count. Failure codes are
    /// implementation-specific; the <c>NotImplementedExternalSearchEngine</c> throws
    /// rather than returning a failure so the call-site bug is loud rather than
    /// silent.
    /// </returns>
    Task<Result<FullTextSearchResultDto>> SearchAsync(
        string indexName,
        string query,
        int skip,
        int take,
        CancellationToken ct);
}
