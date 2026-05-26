using Cnas.Ps.Application.Search;
using Cnas.Ps.Contracts.Search;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Search;

/// <summary>
/// R0522 / TOR CF 03.03 — placeholder adapter that documents the gap to a real
/// Elasticsearch / Solr deployment. Throws <see cref="NotImplementedException"/> on
/// first call so a wiring mistake surfaces immediately rather than silently degrading.
/// </summary>
/// <remarks>
/// <para>
/// The class exists so the abstraction (<see cref="IFullTextSearchEngine"/>) can be
/// taken out for a spin in tests and operational runbooks without committing to
/// shipping a real engine in this batch. A future infra-driven batch will replace this
/// type with the production adapter (registered as the singleton via DI in
/// <c>InfrastructureServiceCollectionExtensions</c>); the application-layer code that
/// consumes the abstraction remains unchanged.
/// </para>
/// </remarks>
public sealed class NotImplementedExternalSearchEngine : IFullTextSearchEngine
{
    /// <inheritdoc />
    public string EngineName => "NotImplementedExternal";

    /// <inheritdoc />
    /// <exception cref="NotImplementedException">
    /// Always — the placeholder adapter never returns a real result. Replace this
    /// type with a real adapter (Elasticsearch / Solr / Meilisearch) before
    /// invoking it.
    /// </exception>
    public Task<Result<FullTextSearchResultDto>> SearchAsync(
        string indexName,
        string query,
        int skip,
        int take,
        CancellationToken ct)
    {
        _ = indexName; _ = query; _ = skip; _ = take; _ = ct;
        throw new NotImplementedException(
            "The external full-text search engine is not yet wired. " +
            "Replace NotImplementedExternalSearchEngine with a real IFullTextSearchEngine adapter.");
    }
}
