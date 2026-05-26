using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>UC01 — Explore public content. Anonymous-accessible. CF 01.01 – 01.10.</summary>
public interface IPublicContentService
{
    /// <summary>Lists public content cards with paging, sorting, and full-text search.</summary>
    Task<Result<PagedResult<PublicContentCard>>> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Streams exported public-content results in the requested format (CF 01.07).</summary>
    Task<Result<Stream>> ExportAsync(
        SearchRequest request,
        ExportFormat format,
        CancellationToken cancellationToken = default);
}
