using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Contracts.Search;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.Infrastructure.Search;

/// <summary>
/// R0522 / TOR CF 03.03 — default <see cref="IFullTextSearchEngine"/> adapter backed by
/// Postgres ILIKE + the existing diacritic-folding pipeline. Acts as the bridge until
/// the operational Elasticsearch/Solr deployment lands (separate batch).
/// </summary>
/// <remarks>
/// <para>
/// <b>Index dispatch.</b> The current implementation supports a single registry
/// (<c>"Solicitant"</c>) — the seed surface for cross-registry search. Adding a new
/// registry is a one-line addition to <see cref="SearchAsync"/>.
/// </para>
/// <para>
/// <b>Diacritic + case insensitive.</b> The Postgres path uses the same
/// <c>EF.Functions.ILike(CnasDbFunctions.Unaccent(col), pattern)</c> shape used by
/// <c>SolicitantService.SearchAsync</c>, ensuring the engine's results are consistent
/// with the bespoke list endpoint's matching rules. On the InMemory provider the
/// implementation falls back to <c>DiacriticFolding.Fold</c> + ordinal-ignore-case
/// substring matching identical to <c>QbeToLinqConverter</c>'s in-memory branch.
/// </para>
/// <para>
/// <b>Scope management.</b> The engine is a singleton (stateless, no per-call mutable
/// state) but its DB context dependency is scoped. The constructor accepts an
/// <see cref="IServiceScopeFactory"/> so each <see cref="SearchAsync"/> call opens a
/// fresh DI scope, resolves the scoped <see cref="ICnasDbContext"/>, executes the
/// query, and disposes the scope. The alternate convenience constructor takes a
/// preconstructed DB context (used by tests that already own a DbContext lifecycle).
/// </para>
/// </remarks>
public sealed class PostgresIlikeFullTextSearchEngine : IFullTextSearchEngine
{
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ICnasDbContext? _explicitDb;
    private readonly ISqidService _sqids;

    /// <summary>
    /// Production constructor. Resolves a fresh <see cref="ICnasDbContext"/> from a
    /// DI scope on every call so the singleton lifetime does not capture a scoped
    /// dependency.
    /// </summary>
    /// <param name="scopeFactory">Scope factory used to mint a per-call DI scope.</param>
    /// <param name="sqids">Sqid encoder used to project ids into the wire payload (CLAUDE.md RULE 3).</param>
    public PostgresIlikeFullTextSearchEngine(
        IServiceScopeFactory scopeFactory,
        ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(sqids);
        _scopeFactory = scopeFactory;
        _sqids = sqids;
    }

    /// <summary>
    /// Test-friendly convenience constructor. Takes a preconstructed
    /// <see cref="ICnasDbContext"/> directly — useful when the test fixture already
    /// owns the DbContext lifecycle (e.g. InMemory provider with per-test database
    /// name). Production wiring should prefer the scope-factory overload.
    /// </summary>
    /// <param name="db">Preconstructed DB context abstraction.</param>
    /// <param name="sqids">Sqid encoder used to project ids into the wire payload.</param>
    public PostgresIlikeFullTextSearchEngine(
        ICnasDbContext db,
        ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sqids);
        _explicitDb = db;
        _sqids = sqids;
    }

    /// <inheritdoc />
    public string EngineName => "PostgresIlike";

    /// <inheritdoc />
    public async Task<Result<FullTextSearchResultDto>> SearchAsync(
        string indexName,
        string query,
        int skip,
        int take,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        var trimmed = (query ?? string.Empty).Trim();
        var folded = Cnas.Ps.Application.Search.DiacriticFolding.Fold(trimmed);
        var pageSize = Math.Clamp(take, 1, 200);
        var pageSkip = Math.Max(0, skip);

        if (_explicitDb is not null)
        {
            return await SearchCoreAsync(_explicitDb, indexName, folded, pageSkip, pageSize, ct)
                .ConfigureAwait(false);
        }

        // Production path: open a fresh scope per call to avoid capturing the scoped
        // DbContext lifetime in this singleton.
        using var scope = _scopeFactory!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICnasDbContext>();
        return await SearchCoreAsync(db, indexName, folded, pageSkip, pageSize, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Pure core that does the EF query against a resolved DB context. Split out so
    /// both constructors share the same logic; lets the scope-factory branch open
    /// and close its scope independently.
    /// </summary>
    /// <param name="db">Resolved DB context (either explicit or scope-resolved).</param>
    /// <param name="indexName">Stable index name (registry code).</param>
    /// <param name="folded">Pre-folded query text.</param>
    /// <param name="pageSkip">Validated skip count.</param>
    /// <param name="pageSize">Validated take count.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The search result.</returns>
    private async Task<Result<FullTextSearchResultDto>> SearchCoreAsync(
        ICnasDbContext db,
        string indexName,
        string folded,
        int pageSkip,
        int pageSize,
        CancellationToken ct)
    {
        switch (indexName)
        {
            case "Solicitant":
            {
                IQueryable<Cnas.Ps.Core.Domain.Solicitant> q = db.Solicitants.Where(s => s.IsActive);
                if (!string.IsNullOrEmpty(folded))
                {
                    var relational = IsRelationalProvider(db);
                    if (relational)
                    {
                        var likePattern = Cnas.Ps.Application.Search.WildcardMask.ToLikePattern(folded);
                        q = q.Where(s =>
                            EF.Functions.ILike(CnasDbFunctions.Unaccent(s.DisplayName), likePattern));
                    }
                    else
                    {
                        var regex = Cnas.Ps.Application.Search.WildcardMask.ToRegex(folded);
                        q = q.Where(s => regex.IsMatch(Cnas.Ps.Application.Search.DiacriticFolding.Fold(s.DisplayName)));
                    }
                }
                var total = await q.CountAsync(ct).ConfigureAwait(false);
                var ids = await q.OrderBy(s => s.Id)
                    .Skip(pageSkip).Take(pageSize)
                    .Select(s => s.Id)
                    .ToListAsync(ct).ConfigureAwait(false);
                IReadOnlyList<string> encoded = ids.Select(id => _sqids.Encode(id)).ToList();
                return Result<FullTextSearchResultDto>.Success(
                    new FullTextSearchResultDto(encoded, total));
            }

            default:
                return Result<FullTextSearchResultDto>.Failure(
                    ErrorCodes.NotFound,
                    $"Full-text index '{indexName}' is not registered on the Postgres adapter.");
        }
    }

    /// <summary>
    /// Detects whether the underlying <see cref="ICnasDbContext"/> is backed by a
    /// relational provider (Npgsql in production) vs the InMemory test fake. Mirrors
    /// the seam from <c>SolicitantService.IsRelationalProvider</c>.
    /// </summary>
    /// <param name="db">The application's DB context abstraction.</param>
    /// <returns><see langword="true"/> for Postgres / SQL Server / SQLite; <see langword="false"/> for InMemory.</returns>
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
