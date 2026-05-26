using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Public content service backed by enabled <c>ServicePassport</c> rows. Returns no PII
/// per CF 01.09 / SEC 044.
/// </summary>
public sealed class PublicContentService(ICnasDbContext db, ISqidService sqids) : IPublicContentService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;

    /// <inheritdoc />
    public async Task<Result<PagedResult<PublicContentCard>>> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pageSize = Math.Clamp(request.Page.PageSize, 1, 200);
        var skip = Math.Max(0, request.Page.Page - 1) * pageSize;

        var query = _db.ServicePassports.Where(p => p.IsEnabled && p.IsActive);
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var trimmed = request.Query.Trim();
            // R0162 / CF 03.13 — diacritic-insensitive search. Relational path uses
            // unaccent(col) ILIKE unaccent(pattern); InMemory path folds both sides via
            // DiacriticFolding.Fold. Both NameRo and DescriptionRo carry user-entered
            // diacritics so both columns are folded.
            // R0164 / UI 012 / CF 03.02 — wildcard-mask translation. The user's query is
            // folded first (R0162) then run through WildcardMask which translates '*' →
            // '%' (LIKE) / '.*' (regex) and escapes any literal LIKE wildcards.
            var folded = DiacriticFolding.Fold(trimmed);
            var likeFolded = WildcardMask.ToLikePattern(folded);
            if (IsRelationalProvider(_db))
            {
                query = query.Where(p =>
                    EF.Functions.ILike(CnasDbFunctions.Unaccent(p.NameRo), likeFolded) ||
                    EF.Functions.ILike(CnasDbFunctions.Unaccent(p.DescriptionRo), likeFolded));
            }
            else
            {
                // R0162 InMemory fallback — DiacriticFolding.Fold is a static method
                // the InMemory provider can invoke client-side via its LINQ-to-Objects
                // translator. Keeping the .Where on IQueryable preserves the EF async
                // provider so subsequent LongCountAsync / ToListAsync still translate.
                // R0164 — substring Contains is replaced with WildcardMask.ToRegex so
                // the InMemory branch honours the same mask semantics as the relational
                // path (anchored when '*' is present, unanchored substring otherwise).
                var regex = WildcardMask.ToRegex(folded);
                query = query.Where(p =>
                    regex.IsMatch(DiacriticFolding.Fold(p.NameRo)) ||
                    regex.IsMatch(DiacriticFolding.Fold(p.DescriptionRo)));
            }
        }

        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await query
            .OrderBy(p => p.NameRo)
            .Skip(skip).Take(pageSize)
            .Select(p => new PublicContentCard(
                _sqids.Encode(p.Id),
                p.NameRo,
                p.DescriptionRo.Length > 240 ? p.DescriptionRo.Substring(0, 240) : p.DescriptionRo,
                "service",
                p.UpdatedAtUtc ?? p.CreatedAtUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return Result<PagedResult<PublicContentCard>>.Success(
            new PagedResult<PublicContentCard>(rows, request.Page.Page, pageSize, total));
    }

    /// <inheritdoc />
    public Task<Result<Stream>> ExportAsync(SearchRequest request, ExportFormat format, CancellationToken cancellationToken = default)
    {
        _ = request;
        _ = format;
        _ = cancellationToken;
        // CSV/XLSX/PDF exporters live in the Reporting service. The thin facade keeps callers happy.
        return Task.FromResult(Result<Stream>.Failure(ErrorCodes.Internal, "Use IReportingService for exports."));
    }

    /// <summary>
    /// Detects whether the underlying <see cref="ICnasDbContext"/> is backed by a relational
    /// provider (Npgsql in production) vs the in-memory test fake. This is the single seam
    /// that lets the search query stay native PostgreSQL ILIKE in production while remaining
    /// executable against EF Core InMemory in integration tests.
    /// </summary>
    /// <remarks>
    /// The seam wraps ONLY this branch because <c>EF.Functions.ILike</c> is Postgres-specific;
    /// the rest of <see cref="SearchAsync"/> (the enabled/active filter, the <c>OrderBy</c>,
    /// the <c>Skip</c>/<c>Take</c>, and the <c>Select</c> projection) uses LINQ operators that
    /// translate on every EF Core provider, so they intentionally do not consult this method.
    /// </remarks>
    /// <param name="db">The application's DB context abstraction.</param>
    /// <returns>True for Postgres / SQL Server / SQLite; false for InMemory or other in-process providers.</returns>
    private static bool IsRelationalProvider(ICnasDbContext db)
    {
        if (db is not DbContext concrete)
        {
            return false;
        }
        var providerName = concrete.Database.ProviderName ?? string.Empty;
        return !providerName.Contains("InMemory", StringComparison.OrdinalIgnoreCase);
    }
}
