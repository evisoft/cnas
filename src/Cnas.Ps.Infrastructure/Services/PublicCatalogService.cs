using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.PublicCatalog;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0502 / R0504 / R0505 / TOR CF 01.05 / CF 01.06 / CF 01.08 — production
/// implementation of <see cref="IPublicCatalogService"/>. Backs the public,
/// anonymous services-catalog browse + export endpoints under
/// <c>/api/public-catalog</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-request lifetime.</b> Registered <c>Scoped</c> so the instance-level
/// <see cref="LastBudgetVerdict"/> slot can carry the most-recent verdict back
/// to the controller without inventing a side-channel — see
/// <see cref="SolicitantService"/> for the established pattern.
/// </para>
/// <para>
/// <b>Postgres vs InMemory.</b> The relational path uses
/// <c>EF.Functions.ILike(unaccent(col), unaccent(@p))</c> for the free-text match
/// and a SQL-translatable <c>CASE</c> expression for the relevance score; the
/// InMemory test path falls back to <see cref="DiacriticFolding.Fold"/> with a
/// client-side <c>StartsWith</c>/<c>Contains</c> heuristic. Both branches produce
/// the same ordering for the test suite to assert against.
/// </para>
/// </remarks>
public sealed class PublicCatalogService(
    ICnasDbContext db,
    ISqidService sqids,
    IQueryBudgetService budget) : IPublicCatalogService
{
    /// <summary>Database context abstraction (per-request).</summary>
    private readonly ICnasDbContext _db = db;

    /// <summary>Sqid encoder used for the projected list-item ids.</summary>
    private readonly ISqidService _sqids = sqids;

    /// <summary>Query-budget guard consulted before materialisation.</summary>
    private readonly IQueryBudgetService _budget = budget;

    /// <inheritdoc />
    public QueryBudgetVerdict? LastBudgetVerdict { get; private set; }

    /// <inheritdoc />
    public async Task<Result<PagedResult<PublicCatalogListItemDto>>> ListAsync(
        PublicCatalogListQueryDto query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Reset the verdict slot so a stale verdict from a prior call on the same
        // service instance cannot leak into a fresh response.
        LastBudgetVerdict = null;

        if (!TryParseSort(query.Sort, out var sort))
        {
            return Result<PagedResult<PublicCatalogListItemDto>>.Failure(
                ErrorCodes.ValidationFailed,
                $"Unknown sort key '{query.Sort}'. Expected Relevance, Alphabetical, Created, or Updated.");
        }

        var (filtered, ctx, foldedQ) = BuildFilteredQuery(query);

        // Budget gate — count over the FILTERED query (pre-Skip / pre-Take). The
        // verdict is cached on LastBudgetVerdict so the controller can populate
        // ProblemDetails extensions even on the failure path.
        var verdict = await _budget.EvaluateAsync(
            QueryBudgetRegistries.PublicCatalog,
            filtered,
            ctx,
            ct).ConfigureAwait(false);
        LastBudgetVerdict = verdict;

        if (!verdict.Allowed)
        {
            return Result<PagedResult<PublicCatalogListItemDto>>.Failure(
                ErrorCodes.QueryTooBroad,
                QueryBudgetFailureEnvelope.FailureMessage);
        }

        // Apply ordering + paging. Server-side Take cap mirrors the validator's
        // hard upper bound (200) as defense-in-depth.
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take, 1, 200);
        var hasQ = !string.IsNullOrEmpty(foldedQ);

        // Relevance with Q: materialise the (budget-capped) filtered set in
        // FILTERED order, then re-order in-process by the relevance score (the
        // SQL providers we support cannot all translate the score expression
        // portably). Without Q, ApplySort returns the SQL-translatable fallback
        // and we paginate on the DB side as usual.
        if (sort == PublicCatalogSortOptions.Relevance && hasQ)
        {
            var allRows = await MaterialiseAsync(filtered, ct).ConfigureAwait(false);
            var ranked = allRows
                .OrderByDescending(r => RelevanceScore(r, foldedQ!))
                .ThenBy(r => r.NameRo, StringComparer.Ordinal)
                .ThenBy(r => r.Id)
                .Skip(skip)
                .Take(take)
                .ToList();
            var lang = NormaliseLanguage(query.Language);
            var items = ranked.Select(r => ToDto(r, lang)).ToList();
            return Result<PagedResult<PublicCatalogListItemDto>>.Success(
                new PagedResult<PublicCatalogListItemDto>(
                    items,
                    Page: (skip / Math.Max(1, take)) + 1,
                    PageSize: take,
                    TotalCount: verdict.EstimatedRowCount));
        }

        var ordered = ApplySort(filtered, sort, hasQ);
        var rows = await ordered
            .Skip(skip)
            .Take(take)
            .Select(p => new ProjectedRow(
                p.Id,
                p.Code,
                p.NameRo,
                p.NameEn,
                p.NameRu,
                p.DescriptionRo,
                p.Category,
                p.Version,
                p.UpdatedAtUtc,
                p.CreatedAtUtc))
            .ToListAsync(ct).ConfigureAwait(false);

        var langDefault = NormaliseLanguage(query.Language);
        var itemsDefault = rows.Select(r => ToDto(r, langDefault)).ToList();

        return Result<PagedResult<PublicCatalogListItemDto>>.Success(
            new PagedResult<PublicCatalogListItemDto>(
                itemsDefault,
                Page: (skip / Math.Max(1, take)) + 1,
                PageSize: take,
                TotalCount: verdict.EstimatedRowCount));
    }

    /// <summary>
    /// Materialises the filtered query into a flat <see cref="ProjectedRow"/>
    /// list. Used by the Relevance + Q path and by the CSV export — both consume
    /// the whole filtered set (the budget guard has already capped it) and apply
    /// downstream ordering in process.
    /// </summary>
    /// <param name="filtered">Filtered passports queryable.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Materialised projected rows.</returns>
    private static Task<List<ProjectedRow>> MaterialiseAsync(
        IQueryable<ServicePassport> filtered,
        CancellationToken ct) =>
        filtered
            .Select(p => new ProjectedRow(
                p.Id,
                p.Code,
                p.NameRo,
                p.NameEn,
                p.NameRu,
                p.DescriptionRo,
                p.Category,
                p.Version,
                p.UpdatedAtUtc,
                p.CreatedAtUtc))
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<Result<byte[]>> ExportCsvAsync(
        PublicCatalogListQueryDto query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        LastBudgetVerdict = null;

        if (!TryParseSort(query.Sort, out var sort))
        {
            return Result<byte[]>.Failure(
                ErrorCodes.ValidationFailed,
                $"Unknown sort key '{query.Sort}'. Expected Relevance, Alphabetical, Created, or Updated.");
        }

        var (filtered, ctx, foldedQ) = BuildFilteredQuery(query);

        var verdict = await _budget.EvaluateAsync(
            QueryBudgetRegistries.PublicCatalog,
            filtered,
            ctx,
            ct).ConfigureAwait(false);
        LastBudgetVerdict = verdict;

        if (!verdict.Allowed)
        {
            return Result<byte[]>.Failure(
                ErrorCodes.QueryTooBroad,
                QueryBudgetFailureEnvelope.FailureMessage);
        }

        // Export the WHOLE filtered set (no Skip / no Take). The budget guard
        // above already refused over-budget exports, so the materialised set is
        // bounded by the policy.
        var hasQ = !string.IsNullOrEmpty(foldedQ);
        List<ProjectedRow> rows;
        if (sort == PublicCatalogSortOptions.Relevance && hasQ)
        {
            var allRows = await MaterialiseAsync(filtered, ct).ConfigureAwait(false);
            rows = allRows
                .OrderByDescending(r => RelevanceScore(r, foldedQ!))
                .ThenBy(r => r.NameRo, StringComparer.Ordinal)
                .ThenBy(r => r.Id)
                .ToList();
        }
        else
        {
            var ordered = ApplySort(filtered, sort, hasQ);
            rows = await ordered
                .Select(p => new ProjectedRow(
                    p.Id,
                    p.Code,
                    p.NameRo,
                    p.NameEn,
                    p.NameRu,
                    p.DescriptionRo,
                    p.Category,
                    p.Version,
                    p.UpdatedAtUtc,
                    p.CreatedAtUtc))
                .ToListAsync(ct).ConfigureAwait(false);
        }

        var lang = NormaliseLanguage(query.Language);
        var items = rows.Select(r => ToDto(r, lang)).ToList();

        var csv = PublicCatalogCsvWriter.Write(items);
        return Result<byte[]>.Success(csv);
    }

    /// <summary>
    /// Builds the IsCurrent + IsActive base query, applies the diacritic-aware
    /// <c>Q</c> filter and the <c>Category</c> equality filter, and returns the
    /// filtered query together with the <see cref="QueryFilterContext"/> the
    /// budget guard consumes plus the folded query string (for the relevance
    /// score later).
    /// </summary>
    /// <param name="query">Inbound filter envelope.</param>
    /// <returns>Filtered queryable + filter context + folded query string.</returns>
    private (IQueryable<ServicePassport> Filtered, QueryFilterContext Ctx, string? FoldedQ) BuildFilteredQuery(
        PublicCatalogListQueryDto query)
    {
        IQueryable<ServicePassport> q = _db.ServicePassports.Where(p => p.IsCurrent && p.IsActive);
        var ctx = new QueryFilterContext();
        string? foldedQ = null;

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var trimmed = query.Q.Trim();
            ctx = ctx.With("Q", trimmed);
            foldedQ = DiacriticFolding.Fold(trimmed);

            if (IsRelationalProvider(_db))
            {
                var likePattern = WildcardMask.ToLikePattern(foldedQ);
                q = q.Where(p =>
                    EF.Functions.ILike(CnasDbFunctions.Unaccent(p.NameRo), likePattern) ||
                    EF.Functions.ILike(CnasDbFunctions.Unaccent(p.DescriptionRo), likePattern));
            }
            else
            {
                // InMemory fallback — DiacriticFolding.Fold + substring match
                // mirrors the R0162 behaviour from PublicContentService.
                var foldedSnapshot = foldedQ;
                q = q.Where(p =>
                    DiacriticFolding.Fold(p.NameRo).Contains(foldedSnapshot, StringComparison.OrdinalIgnoreCase) ||
                    DiacriticFolding.Fold(p.DescriptionRo).Contains(foldedSnapshot, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            var category = query.Category.Trim();
            ctx = ctx.With("Category", category);
            q = q.Where(p => p.Category == category);
        }

        return (q, ctx, foldedQ);
    }

    /// <summary>
    /// Applies the sort key to the filtered query. <see cref="PublicCatalogSortOptions.Relevance"/>
    /// is handled by the caller via in-process scoring (see <see cref="RelevanceScore"/>);
    /// this helper handles only the SQL-translatable cases (Alphabetical / Created /
    /// Updated) and the no-Q relevance fallback.
    /// </summary>
    /// <param name="query">Filtered passports queryable.</param>
    /// <param name="sort">Resolved sort option.</param>
    /// <param name="hasQ">Whether the caller supplied a non-empty Q filter.</param>
    /// <returns>The ordered queryable, ready for Skip/Take.</returns>
    private static IOrderedQueryable<ServicePassport> ApplySort(
        IQueryable<ServicePassport> query,
        PublicCatalogSortOptions sort,
        bool hasQ)
    {
        return sort switch
        {
            PublicCatalogSortOptions.Alphabetical =>
                query.OrderBy(p => p.NameRo).ThenBy(p => p.Id),

            PublicCatalogSortOptions.Created =>
                query.OrderByDescending(p => p.CreatedAtUtc).ThenByDescending(p => p.Id),

            PublicCatalogSortOptions.Updated =>
                query.OrderByDescending(p => p.UpdatedAtUtc ?? p.CreatedAtUtc)
                     .ThenByDescending(p => p.Id),

            // Relevance with no Q is meaningless — fall through to Updated DESC
            // (same default the UI's empty-query landing page expects). Relevance
            // WITH Q is scored client-side (see ListAsync / ExportCsvAsync) once
            // the rows have been materialised, because the unaccent + ILIKE
            // expression we'd need for SQL-side scoring is not portable to the
            // InMemory provider.
            _ when !hasQ =>
                query.OrderByDescending(p => p.UpdatedAtUtc ?? p.CreatedAtUtc)
                     .ThenByDescending(p => p.Id),

            _ =>
                query.OrderBy(p => p.NameRo).ThenBy(p => p.Id),
        };
    }

    /// <summary>
    /// Computes the in-process relevance score for a single projected row. Higher
    /// = more relevant. Used by <see cref="ListAsync"/> / <see cref="ExportCsvAsync"/>
    /// after materialisation when the caller requested
    /// <see cref="PublicCatalogSortOptions.Relevance"/> with a non-empty Q.
    /// </summary>
    /// <param name="row">Materialised row.</param>
    /// <param name="foldedQ">Folded query string (non-empty).</param>
    /// <returns>3 = NameRo starts with Q; 2 = NameRo contains Q; 1 = description contains Q; 0 = neither.</returns>
    private static int RelevanceScore(ProjectedRow row, string foldedQ)
    {
        var name = DiacriticFolding.Fold(row.NameRo);
        if (name.StartsWith(foldedQ, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }
        if (name.Contains(foldedQ, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }
        var description = DiacriticFolding.Fold(row.DescriptionRo);
        return description.Contains(foldedQ, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    /// <summary>
    /// Projects a row to the wire DTO with locale-resolved Name + Description and
    /// the Sqid-encoded id. RO is the default; missing translations fall back to
    /// RO so the public catalogue never renders an empty card.
    /// </summary>
    /// <param name="row">Intermediate projection materialised from the DB.</param>
    /// <param name="language">Normalised ISO-639-1 code (<c>ro</c>/<c>en</c>/<c>ru</c>).</param>
    /// <returns>The wire DTO.</returns>
    private PublicCatalogListItemDto ToDto(ProjectedRow row, string language)
    {
        // Name selection — fall back to RO when the requested locale's column is
        // null or empty. DescriptionRo is the only description column on the
        // entity today (R0133 TemplateVariants are for DocumentTemplates, not
        // ServicePassports), so the description column is locale-agnostic.
        var name = language switch
        {
            "en" => !string.IsNullOrWhiteSpace(row.NameEn) ? row.NameEn! : row.NameRo,
            "ru" => !string.IsNullOrWhiteSpace(row.NameRu) ? row.NameRu! : row.NameRo,
            _ => row.NameRo,
        };

        var updatedAt = row.UpdatedAtUtc ?? row.CreatedAtUtc;

        return new PublicCatalogListItemDto(
            Id: _sqids.Encode(row.Id),
            Code: row.Code,
            Name: name,
            Description: row.DescriptionRo,
            Category: row.Category,
            Version: row.Version,
            UpdatedAtUtc: updatedAt);
    }

    /// <summary>
    /// Parses the inbound <c>Sort</c> string to its enum form. Case-insensitive
    /// to match the validator's tolerance; returns <c>false</c> for unknown
    /// values so the service can fail fast with a structured error.
    /// </summary>
    /// <param name="value">Caller-supplied sort string.</param>
    /// <param name="sort">Parsed enum (default <see cref="PublicCatalogSortOptions.Relevance"/> on null/empty).</param>
    /// <returns><c>true</c> on success; <c>false</c> on an unknown value.</returns>
    private static bool TryParseSort(string? value, out PublicCatalogSortOptions sort)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            sort = PublicCatalogSortOptions.Relevance;
            return true;
        }
        return Enum.TryParse(value, ignoreCase: true, out sort);
    }

    /// <summary>
    /// Normalises the inbound language code to a known set
    /// (<c>"ro"</c>/<c>"en"</c>/<c>"ru"</c>). Unknown or null codes resolve to
    /// the default <c>"ro"</c>.
    /// </summary>
    /// <param name="value">Caller-supplied language code; nullable.</param>
    /// <returns>One of <c>"ro"</c>, <c>"en"</c>, <c>"ru"</c>.</returns>
    private static string NormaliseLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "ro";
        }
        var lower = value.Trim().ToLowerInvariant();
        return lower switch
        {
            "ro" or "en" or "ru" => lower,
            _ => "ro",
        };
    }

    /// <summary>
    /// Detects whether the underlying <see cref="ICnasDbContext"/> is backed by a
    /// relational provider (Npgsql in production) vs the in-memory test fake.
    /// Mirrors the seam from
    /// <see cref="SolicitantService"/> / <see cref="PublicContentService"/>.
    /// </summary>
    /// <param name="db">The application's DB context abstraction.</param>
    /// <returns>True for Postgres / SQL Server / SQLite; false for InMemory.</returns>
    private static bool IsRelationalProvider(ICnasDbContext db)
    {
        if (db is not DbContext concrete)
        {
            return false;
        }
        var providerName = concrete.Database.ProviderName ?? string.Empty;
        return !providerName.Contains("InMemory", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Intermediate flat projection used between EF materialisation and the wire
    /// DTO. Keeps the EF projection trivial (no method calls on the entity) and
    /// localises the language-fallback logic in C#.
    /// </summary>
    /// <param name="Id">Internal database id.</param>
    /// <param name="Code">Stable business code.</param>
    /// <param name="NameRo">Romanian display name (required).</param>
    /// <param name="NameEn">Optional English display name.</param>
    /// <param name="NameRu">Optional Russian display name.</param>
    /// <param name="DescriptionRo">Romanian description (locale-agnostic in current schema).</param>
    /// <param name="Category">Optional category code.</param>
    /// <param name="Version">Revision number.</param>
    /// <param name="UpdatedAtUtc">Last-modified timestamp; nullable.</param>
    /// <param name="CreatedAtUtc">Creation timestamp.</param>
    private sealed record ProjectedRow(
        long Id,
        string Code,
        string NameRo,
        string? NameEn,
        string? NameRu,
        string DescriptionRo,
        string? Category,
        int Version,
        DateTime? UpdatedAtUtc,
        DateTime CreatedAtUtc);
}

/// <summary>
/// R0505 / TOR CF 01.08 — RFC 4180 CSV writer for the public services-catalog
/// export. UTF-8 with byte-order mark (BOM) so Excel detects the encoding
/// without a manual import-wizard step; commas, double quotes, and newlines
/// inside fields are quoted per RFC 4180.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a bespoke writer.</b> A full CSV library (CsvHelper, Sylvan) would be
/// overkill for the six-column export; the formatting rules are mechanical and
/// the assembly footprint of an extra package is not worth it for one endpoint.
/// </para>
/// <para>
/// <b>Header row.</b> <c>Code,Name,Description,Category,Version,UpdatedAtUtc</c> —
/// stable across versions. Renaming a column is a breaking change for downstream
/// consumers (Excel pivot models, analytics jobs).
/// </para>
/// </remarks>
public static class PublicCatalogCsvWriter
{
    /// <summary>UTF-8 BOM bytes (Excel uses these to detect the encoding).</summary>
    private static readonly byte[] s_utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF };

    /// <summary>
    /// Cached <see cref="System.Buffers.SearchValues{Char}"/> instance for the
    /// RFC 4180 "needs quoting" character set. Created once and reused on every
    /// call to <see cref="Quote"/> (CA1870).
    /// </summary>
    private static readonly System.Buffers.SearchValues<char> s_quoteTriggers =
        System.Buffers.SearchValues.Create(",\"\r\n");

    /// <summary>The stable header row written before the data lines.</summary>
    public const string Header = "Code,Name,Description,Category,Version,UpdatedAtUtc";

    /// <summary>
    /// Serialises <paramref name="rows"/> to a CSV byte array suitable for
    /// returning from an MVC action with content type
    /// <c>text/csv; charset=utf-8</c>.
    /// </summary>
    /// <param name="rows">Rows to write; order is preserved.</param>
    /// <returns>A UTF-8 byte array starting with the BOM.</returns>
    public static byte[] Write(IEnumerable<PublicCatalogListItemDto> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var sb = new StringBuilder();
        sb.AppendLine(Header);

        foreach (var row in rows)
        {
            sb.Append(Quote(row.Code));
            sb.Append(',');
            sb.Append(Quote(row.Name));
            sb.Append(',');
            sb.Append(Quote(row.Description ?? string.Empty));
            sb.Append(',');
            sb.Append(Quote(row.Category ?? string.Empty));
            sb.Append(',');
            // Version is always a non-negative integer — no quoting required, and
            // CSV invariant-culture formatting is enforced by passing the integer
            // through ToString(InvariantCulture).
            sb.Append(row.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(',');
            // ISO 8601 round-trip ("O") format keeps the wire form locale-stable
            // and round-trippable through Excel + analytics tooling.
            sb.Append(row.UpdatedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            sb.AppendLine();
        }

        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var combined = new byte[s_utf8Bom.Length + body.Length];
        Buffer.BlockCopy(s_utf8Bom, 0, combined, 0, s_utf8Bom.Length);
        Buffer.BlockCopy(body, 0, combined, s_utf8Bom.Length, body.Length);
        return combined;
    }

    /// <summary>
    /// Applies RFC 4180 quoting to <paramref name="value"/>: wraps the value in
    /// double quotes when it contains a comma, double quote, CR, or LF; embedded
    /// double quotes are escaped by doubling them.
    /// </summary>
    /// <param name="value">Cell value (never null — caller substitutes empty string).</param>
    /// <returns>The quoted (or unquoted, when safe) cell value.</returns>
    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        var needsQuoting = value.AsSpan().ContainsAny(s_quoteTriggers);
        if (!needsQuoting)
        {
            return value;
        }
        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
