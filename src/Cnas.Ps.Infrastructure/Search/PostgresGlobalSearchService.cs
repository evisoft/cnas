using System.Collections.Generic;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cnas.Ps.Infrastructure.Search;

/// <summary>
/// R0160 / R0161 / TOR CF 03.03 — production global full-text-search service. Fans
/// out across the five canonical domains (applications, contributors,
/// insured-persons, documents, dossiers) and merges the per-domain hits into a
/// single globally-ranked result list.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provider strategy.</b> On the Npgsql relational provider every per-domain
/// query runs raw SQL against a <c>tsvector</c>-backed generated column ("search_vector")
/// — see the <c>20260524_AddFullTextSearchIndexes</c> migration for the column
/// definitions and GIN indexes. Ranking is <c>ts_rank_cd(search_vector,
/// plainto_tsquery('romanian', @q))</c>. On the EF Core InMemory provider the
/// service falls back to lowercase-substring filtering with a per-row rank equal
/// to <c>(match_count) / (haystack_length)</c> so the test suite remains
/// deterministic without spinning a Postgres container.
/// </para>
/// <para>
/// <b>Romanian-locale FTS config.</b> PostgreSQL 16 ships the <c>romanian</c>
/// text-search configuration out of the box (it is registered in
/// <c>pg_ts_config</c> on every fresh cluster). The migration uses
/// <c>setweight(to_tsvector('romanian', ...), 'A'|'B'|'C')</c> so codes
/// (ApplicationNumber, Idno, Idnp, DossierNumber) carry the highest weight,
/// names / titles weight 'B', and descriptive notes weight 'C'.
/// </para>
/// <para>
/// <b>Pure read.</b> The service is marked
/// <see cref="LongRunningReportServiceAttribute"/>: it consumes the
/// <see cref="IReadOnlyCnasDbContext"/> seam so the per-domain queries land on
/// the streaming-replication follower per R0026 / ARH 025, leaving the writable
/// primary for write workloads. It performs no mutations.
/// </para>
/// <para>
/// <b>Parameter binding.</b> User-supplied query text is bound through Npgsql
/// parameters (<c>@q</c>) — never string-interpolated into the SQL — so SQL
/// injection is impossible even though the per-domain queries use
/// <c>FromSqlRaw</c>.
/// </para>
/// </remarks>
[LongRunningReportService]
public sealed class PostgresGlobalSearchService : IGlobalSearchService
{
    private readonly IReadOnlyCnasDbContext _db;
    private readonly ISqidService _sqids;

    /// <summary>Hard ceiling on the per-domain hit list (sliced before merging).</summary>
    /// <remarks>
    /// Each domain query asks for at most <see cref="PerDomainCap"/> rows so the
    /// merge step has a bounded working set even if one domain dominates. The
    /// final response is sliced to <c>input.Take</c> after the global sort.
    /// </remarks>
    public const int PerDomainCap = 200;

    /// <summary>
    /// Creates the search service.
    /// </summary>
    /// <param name="db">Read-only DB context routed to the streaming-replica.</param>
    /// <param name="sqids">Sqid encoder used to project ids into the wire payload (CLAUDE.md RULE 3).</param>
    public PostgresGlobalSearchService(IReadOnlyCnasDbContext db, ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sqids);
        _db = db;
        _sqids = sqids;
    }

    /// <inheritdoc />
    public async Task<Result<GlobalSearchResultDto>> SearchAsync(
        GlobalSearchInputDto input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Service-level guard — mirrors the validator so a controller that skips
        // it still surfaces a clean VALIDATION_FAILED.
        if (string.IsNullOrWhiteSpace(input.Query))
        {
            return Result<GlobalSearchResultDto>.Failure(
                ErrorCodes.ValidationFailed,
                "Query is required.");
        }

        if (input.Query.Length > GlobalSearchInputValidator.MaxQueryLength)
        {
            return Result<GlobalSearchResultDto>.Failure(
                ErrorCodes.ValidationFailed,
                $"Query must be at most {GlobalSearchInputValidator.MaxQueryLength} characters.");
        }

        var skip = Math.Max(0, input.Skip);
        var take = Math.Clamp(input.Take, 1, GlobalSearchInputValidator.MaxTake);

        var domains = NormaliseDomains(input.Domains);
        if (domains.Count == 0)
        {
            return Result<GlobalSearchResultDto>.Failure(
                ErrorCodes.ValidationFailed,
                "At least one valid domain must be selected.");
        }

        var query = input.Query.Trim();
        var isRelational = IsRelationalProvider(_db);

        // Fan out across domains in parallel. Each branch returns a per-domain
        // list bounded by PerDomainCap; we merge + globally sort below.
        var tasks = new List<Task<IReadOnlyList<GlobalSearchHitDto>>>(domains.Count);
        foreach (var domain in domains)
        {
            tasks.Add(SearchDomainAsync(domain, query, isRelational, ct));
        }

        var perDomainResults = await Task.WhenAll(tasks).ConfigureAwait(false);
        var merged = new List<GlobalSearchHitDto>();
        foreach (var list in perDomainResults)
        {
            merged.AddRange(list);
        }

        // Globally sort by descending rank — Postgres ts_rank_cd is provider-local
        // so we cannot trust cross-provider absolute comparability, but within a
        // single request all branches use the same provider.
        merged.Sort(static (a, b) => b.Rank.CompareTo(a.Rank));

        var totalHits = merged.Count;
        var paged = merged.Skip(skip).Take(take).ToList();

        CnasMeter.FullTextSearchExecuted.Add(
            1,
            new KeyValuePair<string, object?>("domain_count", domains.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (totalHits == 0)
        {
            CnasMeter.FullTextSearchEmptyResult.Add(1);
        }

        return Result<GlobalSearchResultDto>.Success(
            new GlobalSearchResultDto(totalHits, paged, skip, take));
    }

    /// <summary>
    /// Normalises the supplied domain filter into a case-folded, deduplicated,
    /// validation-filtered list. Null / empty input expands to every canonical
    /// domain.
    /// </summary>
    /// <param name="raw">User-supplied domain filter.</param>
    /// <returns>The validated, lowercased domain list.</returns>
    private static IReadOnlyList<string> NormaliseDomains(IReadOnlyList<string>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return GlobalSearchDomains.All;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(raw.Count);
        foreach (var entry in raw)
        {
            if (!GlobalSearchInputValidator.IsKnownDomain(entry))
            {
                continue;
            }

            var canonical = MatchCanonical(entry);
            if (canonical is not null && seen.Add(canonical))
            {
                result.Add(canonical);
            }
        }

        return result;
    }

    /// <summary>Returns the canonical (lowercased) spelling of a known domain, or null.</summary>
    /// <param name="raw">User-supplied domain code.</param>
    /// <returns>The canonical lowercased spelling, or <see langword="null"/> when unknown.</returns>
    private static string? MatchCanonical(string raw)
    {
        foreach (var canonical in GlobalSearchDomains.All)
        {
            if (string.Equals(canonical, raw, StringComparison.OrdinalIgnoreCase))
            {
                return canonical;
            }
        }

        return null;
    }

    /// <summary>
    /// Routes a per-domain search through either the relational <c>tsvector</c>
    /// path or the InMemory substring fallback.
    /// </summary>
    /// <param name="domain">Canonical lowercase domain code.</param>
    /// <param name="query">Trimmed user query.</param>
    /// <param name="isRelational">True when running on Npgsql; false on InMemory.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The per-domain hit list (bounded by <see cref="PerDomainCap"/>).</returns>
    private async Task<IReadOnlyList<GlobalSearchHitDto>> SearchDomainAsync(
        string domain,
        string query,
        bool isRelational,
        CancellationToken ct)
    {
        return domain switch
        {
            GlobalSearchDomains.Applications => isRelational
                ? await ApplicationsRelationalAsync(query, ct).ConfigureAwait(false)
                : await ApplicationsInMemoryAsync(query, ct).ConfigureAwait(false),
            GlobalSearchDomains.Contributors => isRelational
                ? await ContributorsRelationalAsync(query, ct).ConfigureAwait(false)
                : await ContributorsInMemoryAsync(query, ct).ConfigureAwait(false),
            GlobalSearchDomains.InsuredPersons => isRelational
                ? await InsuredPersonsRelationalAsync(query, ct).ConfigureAwait(false)
                : await InsuredPersonsInMemoryAsync(query, ct).ConfigureAwait(false),
            GlobalSearchDomains.Documents => isRelational
                ? await DocumentsRelationalAsync(query, ct).ConfigureAwait(false)
                : await DocumentsInMemoryAsync(query, ct).ConfigureAwait(false),
            GlobalSearchDomains.Dossiers => isRelational
                ? await DossiersRelationalAsync(query, ct).ConfigureAwait(false)
                : await DossiersInMemoryAsync(query, ct).ConfigureAwait(false),
            _ => Array.Empty<GlobalSearchHitDto>(),
        };
    }

    // ─────────────────────── relational (Npgsql / tsvector) branches ───────────────────────

    /// <summary>
    /// Returns a parameter envelope for the FTS query (<c>plainto_tsquery</c> uses
    /// the user query verbatim — Postgres parses it server-side into a safe
    /// tsquery, so injection via the parameter is impossible).
    /// </summary>
    /// <param name="query">User query (already trimmed).</param>
    /// <returns>The Npgsql parameter bound to <c>@q</c>.</returns>
    private static NpgsqlParameter QueryParam(string query)
        => new("q", NpgsqlTypes.NpgsqlDbType.Text) { Value = query };

    /// <summary>Runs the Applications FTS query on the relational provider.</summary>
    /// <param name="query">User query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Per-domain hit list.</returns>
    private async Task<IReadOnlyList<GlobalSearchHitDto>> ApplicationsRelationalAsync(
        string query,
        CancellationToken ct)
    {
        // ReferenceNumber is the only free-text column on Applications. The
        // tsvector is built from coalesce(ReferenceNumber, '') with weight 'A'.
        var sql =
            @"SELECT a.""Id"" AS Id,
                     COALESCE(a.""ReferenceNumber"", '') AS Title,
                     COALESCE(a.""ReferenceNumber"", '') AS Snippet,
                     ts_rank_cd(a.search_vector, plainto_tsquery('romanian', @q)) AS Rank
              FROM cnas.""Applications"" a
              WHERE a.""IsActive"" = TRUE
                AND a.search_vector @@ plainto_tsquery('romanian', @q)
              ORDER BY Rank DESC, a.""Id""
              LIMIT " + PerDomainCap;

        var rows = await ExecuteRelationalAsync(sql, query, ct).ConfigureAwait(false);
        return rows
            .Select(r => new GlobalSearchHitDto(
                GlobalSearchDomains.Applications,
                _sqids.Encode(r.Id),
                r.Title,
                r.Snippet,
                r.Rank))
            .ToList();
    }

    /// <summary>Runs the Contributors FTS query on the relational provider.</summary>
    /// <param name="query">User query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Per-domain hit list.</returns>
    private async Task<IReadOnlyList<GlobalSearchHitDto>> ContributorsRelationalAsync(
        string query,
        CancellationToken ct)
    {
        var sql =
            @"SELECT c.""Id"" AS Id,
                     c.""Denumire"" AS Title,
                     COALESCE(c.""Idno"", '') AS Snippet,
                     ts_rank_cd(c.search_vector, plainto_tsquery('romanian', @q)) AS Rank
              FROM cnas.""Contributors"" c
              WHERE c.""IsActive"" = TRUE
                AND c.search_vector @@ plainto_tsquery('romanian', @q)
              ORDER BY Rank DESC, c.""Id""
              LIMIT " + PerDomainCap;

        var rows = await ExecuteRelationalAsync(sql, query, ct).ConfigureAwait(false);
        return rows
            .Select(r => new GlobalSearchHitDto(
                GlobalSearchDomains.Contributors,
                _sqids.Encode(r.Id),
                r.Title,
                r.Snippet,
                r.Rank))
            .ToList();
    }

    /// <summary>Runs the InsuredPersons FTS query on the relational provider.</summary>
    /// <param name="query">User query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Per-domain hit list.</returns>
    private async Task<IReadOnlyList<GlobalSearchHitDto>> InsuredPersonsRelationalAsync(
        string query,
        CancellationToken ct)
    {
        var sql =
            @"SELECT p.""Id"" AS Id,
                     (p.""LastName"" || ' ' || p.""FirstName"") AS Title,
                     COALESCE(p.""Idnp"", '') AS Snippet,
                     ts_rank_cd(p.search_vector, plainto_tsquery('romanian', @q)) AS Rank
              FROM cnas.""InsuredPersons"" p
              WHERE p.""IsActive"" = TRUE
                AND p.search_vector @@ plainto_tsquery('romanian', @q)
              ORDER BY Rank DESC, p.""Id""
              LIMIT " + PerDomainCap;

        var rows = await ExecuteRelationalAsync(sql, query, ct).ConfigureAwait(false);
        return rows
            .Select(r => new GlobalSearchHitDto(
                GlobalSearchDomains.InsuredPersons,
                _sqids.Encode(r.Id),
                r.Title,
                r.Snippet,
                r.Rank))
            .ToList();
    }

    /// <summary>Runs the Documents FTS query on the relational provider.</summary>
    /// <param name="query">User query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Per-domain hit list.</returns>
    private async Task<IReadOnlyList<GlobalSearchHitDto>> DocumentsRelationalAsync(
        string query,
        CancellationToken ct)
    {
        var sql =
            @"SELECT d.""Id"" AS Id,
                     d.""Title"" AS Title,
                     COALESCE(d.""VerdictNote"", '') AS Snippet,
                     ts_rank_cd(d.search_vector, plainto_tsquery('romanian', @q)) AS Rank
              FROM cnas.""Documents"" d
              WHERE d.""IsActive"" = TRUE
                AND d.search_vector @@ plainto_tsquery('romanian', @q)
              ORDER BY Rank DESC, d.""Id""
              LIMIT " + PerDomainCap;

        var rows = await ExecuteRelationalAsync(sql, query, ct).ConfigureAwait(false);
        return rows
            .Select(r => new GlobalSearchHitDto(
                GlobalSearchDomains.Documents,
                _sqids.Encode(r.Id),
                r.Title,
                r.Snippet,
                r.Rank))
            .ToList();
    }

    /// <summary>Runs the Dossiers FTS query on the relational provider.</summary>
    /// <param name="query">User query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Per-domain hit list.</returns>
    private async Task<IReadOnlyList<GlobalSearchHitDto>> DossiersRelationalAsync(
        string query,
        CancellationToken ct)
    {
        var sql =
            @"SELECT d.""Id"" AS Id,
                     d.""DossierNumber"" AS Title,
                     d.""DossierNumber"" AS Snippet,
                     ts_rank_cd(d.search_vector, plainto_tsquery('romanian', @q)) AS Rank
              FROM cnas.""Dossiers"" d
              WHERE d.""IsActive"" = TRUE
                AND d.search_vector @@ plainto_tsquery('romanian', @q)
              ORDER BY Rank DESC, d.""Id""
              LIMIT " + PerDomainCap;

        var rows = await ExecuteRelationalAsync(sql, query, ct).ConfigureAwait(false);
        return rows
            .Select(r => new GlobalSearchHitDto(
                GlobalSearchDomains.Dossiers,
                _sqids.Encode(r.Id),
                r.Title,
                r.Snippet,
                r.Rank))
            .ToList();
    }

    /// <summary>
    /// Shared raw-SQL execution helper. The user query is bound through an Npgsql
    /// parameter — never string-interpolated into the SQL — so SQL injection is
    /// impossible.
    /// </summary>
    /// <param name="sql">Parameterised SQL with a <c>@q</c> placeholder.</param>
    /// <param name="query">User query value.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The raw row buffer the per-domain projection wraps.</returns>
    private async Task<IReadOnlyList<RawHit>> ExecuteRelationalAsync(
        string sql,
        string query,
        CancellationToken ct)
    {
        if (_db is not DbContext concrete)
        {
            return Array.Empty<RawHit>();
        }

        var rows = await concrete.Database
            .SqlQueryRaw<RawHit>(sql, QueryParam(query))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows;
    }

    /// <summary>Raw SQL projection row materialised by <see cref="ExecuteRelationalAsync"/>.</summary>
    /// <param name="Id">Underlying row identifier (raw long).</param>
    /// <param name="Title">Best human-readable title.</param>
    /// <param name="Snippet">Short surrounding text excerpt.</param>
    /// <param name="Rank">ts_rank_cd value from Postgres.</param>
    public sealed record RawHit(long Id, string Title, string Snippet, double Rank);

    // ─────────────────────── InMemory fallback branches ───────────────────────

    /// <summary>InMemory rank: occurrence count divided by haystack length (zero when no match).</summary>
    /// <param name="haystack">Source column (lowercased title / snippet).</param>
    /// <param name="needle">Lowercased query.</param>
    /// <returns>Synthetic rank, mimicking ts_rank_cd shape.</returns>
    private static double InMemoryRank(string? haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
        {
            return 0d;
        }

        var lowerHaystack = haystack.ToLowerInvariant();
        var lowerNeedle = needle.ToLowerInvariant();
        var index = 0;
        var occurrences = 0;
        while ((index = lowerHaystack.IndexOf(lowerNeedle, index, StringComparison.Ordinal)) >= 0)
        {
            occurrences++;
            index += lowerNeedle.Length;
        }

        if (occurrences == 0)
        {
            return 0d;
        }

        return (double)occurrences / Math.Max(1, lowerHaystack.Length);
    }

    /// <summary>Applications InMemory fallback (substring over ReferenceNumber).</summary>
    /// <param name="query">User query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Per-domain hit list.</returns>
    private async Task<IReadOnlyList<GlobalSearchHitDto>> ApplicationsInMemoryAsync(string query, CancellationToken ct)
    {
        var rows = await _db.Applications
            .Where(a => a.IsActive
                && a.ReferenceNumber != null
                && a.ReferenceNumber.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(PerDomainCap)
            .Select(a => new { a.Id, a.ReferenceNumber })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Select(a => new GlobalSearchHitDto(
                GlobalSearchDomains.Applications,
                _sqids.Encode(a.Id),
                a.ReferenceNumber ?? string.Empty,
                a.ReferenceNumber ?? string.Empty,
                InMemoryRank(a.ReferenceNumber, query)))
            .ToList();
    }

    /// <summary>Contributors InMemory fallback (substring over Denumire / Idno).</summary>
    /// <param name="query">User query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Per-domain hit list.</returns>
    private async Task<IReadOnlyList<GlobalSearchHitDto>> ContributorsInMemoryAsync(string query, CancellationToken ct)
    {
        var rows = await _db.Contributors
            .Where(c => c.IsActive
                && (c.Denumire.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || c.Idno.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Take(PerDomainCap)
            .Select(c => new { c.Id, c.Denumire, c.Idno })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Select(c => new GlobalSearchHitDto(
                GlobalSearchDomains.Contributors,
                _sqids.Encode(c.Id),
                c.Denumire,
                c.Idno,
                Math.Max(InMemoryRank(c.Denumire, query), InMemoryRank(c.Idno, query))))
            .ToList();
    }

    /// <summary>InsuredPersons InMemory fallback (substring over LastName / FirstName / Idnp).</summary>
    /// <param name="query">User query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Per-domain hit list.</returns>
    private async Task<IReadOnlyList<GlobalSearchHitDto>> InsuredPersonsInMemoryAsync(string query, CancellationToken ct)
    {
        var rows = await _db.InsuredPersons
            .Where(p => p.IsActive
                && (p.LastName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || p.FirstName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || p.Idnp.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Take(PerDomainCap)
            .Select(p => new { p.Id, p.LastName, p.FirstName, p.Idnp })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Select(p =>
            {
                var title = p.LastName + " " + p.FirstName;
                var rank = Math.Max(
                    Math.Max(InMemoryRank(p.LastName, query), InMemoryRank(p.FirstName, query)),
                    InMemoryRank(p.Idnp, query));
                return new GlobalSearchHitDto(
                    GlobalSearchDomains.InsuredPersons,
                    _sqids.Encode(p.Id),
                    title,
                    p.Idnp,
                    rank);
            })
            .ToList();
    }

    /// <summary>Documents InMemory fallback (substring over Title / VerdictNote).</summary>
    /// <param name="query">User query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Per-domain hit list.</returns>
    private async Task<IReadOnlyList<GlobalSearchHitDto>> DocumentsInMemoryAsync(string query, CancellationToken ct)
    {
        var rows = await _db.Documents
            .Where(d => d.IsActive
                && (d.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (d.VerdictNote != null && d.VerdictNote.Contains(query, StringComparison.OrdinalIgnoreCase))))
            .Take(PerDomainCap)
            .Select(d => new { d.Id, d.Title, d.VerdictNote })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Select(d => new GlobalSearchHitDto(
                GlobalSearchDomains.Documents,
                _sqids.Encode(d.Id),
                d.Title,
                d.VerdictNote ?? string.Empty,
                Math.Max(InMemoryRank(d.Title, query), InMemoryRank(d.VerdictNote, query))))
            .ToList();
    }

    /// <summary>Dossiers InMemory fallback (substring over DossierNumber).</summary>
    /// <param name="query">User query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Per-domain hit list.</returns>
    private async Task<IReadOnlyList<GlobalSearchHitDto>> DossiersInMemoryAsync(string query, CancellationToken ct)
    {
        var rows = await _db.Dossiers
            .Where(d => d.IsActive
                && d.DossierNumber.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(PerDomainCap)
            .Select(d => new { d.Id, d.DossierNumber })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Select(d => new GlobalSearchHitDto(
                GlobalSearchDomains.Dossiers,
                _sqids.Encode(d.Id),
                d.DossierNumber,
                d.DossierNumber,
                InMemoryRank(d.DossierNumber, query)))
            .ToList();
    }

    /// <summary>
    /// Detects whether the underlying <see cref="IReadOnlyCnasDbContext"/> is
    /// backed by a relational provider (Npgsql in production) vs the InMemory
    /// test fake. Mirrors the seam used by <c>DataSearchService</c>.
    /// </summary>
    /// <param name="db">The application's DB context abstraction.</param>
    /// <returns>True for Postgres / SQL Server / SQLite; false for InMemory.</returns>
    private static bool IsRelationalProvider(IReadOnlyCnasDbContext db)
    {
        if (db is not DbContext concrete)
        {
            return false;
        }
        var providerName = concrete.Database.ProviderName ?? string.Empty;
        return !providerName.Contains("InMemory", StringComparison.OrdinalIgnoreCase);
    }
}
