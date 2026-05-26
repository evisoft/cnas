using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>UC03 / UC12 — search and view data inside the authorised portal.</summary>
public interface IDataSearchService
{
    /// <summary>Search authorised registries with QBE/global filter (UI 009-012).</summary>
    Task<Result<PagedResult<SearchRow>>> SearchAsync(
        string registry,
        SearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Exports a search result in the requested format (UI 013).</summary>
    Task<Result<Stream>> ExportAsync(
        string registry,
        SearchRequest request,
        ExportFormat format,
        CancellationToken cancellationToken = default);
}
