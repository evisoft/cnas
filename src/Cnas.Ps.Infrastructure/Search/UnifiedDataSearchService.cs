using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Search;

/// <summary>
/// R0520 / TOR CF 03.01 — unified cross-entity search service. Projects each of
/// the nine canonical domains (applicants, applications, dossiers, payers,
/// insured persons, tasks, notifications, issued documents, workflow
/// documents) into the homogeneous <see cref="UnifiedSearchHitDto"/> shape so
/// the UI renders every hit through a single template.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only.</b> Marked <see cref="LongRunningReportServiceAttribute"/> so
/// the architecture test enforces it consumes only
/// <see cref="IReadOnlyCnasDbContext"/> (R0026 / ARH 025 — read-replica
/// routing). No mutations.
/// </para>
/// <para>
/// <b>Row-level scoping.</b> Every per-domain projector passes its filtered
/// query through <see cref="ISearchRowLevelFilter.ApplyRowLevelScope{T}"/>
/// BEFORE materialisation so a non-super-role caller only sees rows the ABAC
/// rule set for <c>SEARCH.{DOMAIN}</c> permits (R0526 / CF 03.10).
/// </para>
/// <para>
/// <b>InMemory friendly.</b> Each projector uses provider-portable LINQ
/// operators (no <c>FromSqlRaw</c>) so the EF Core InMemory provider in the
/// integration test fixture exercises the same code path as production.
/// </para>
/// </remarks>
[LongRunningReportService]
public sealed class UnifiedDataSearchService : IUnifiedDataSearchService
{
    /// <summary>Hard cap on the per-domain hit list (sliced before merging).</summary>
    public const int PerDomainCap = 200;

    private readonly IReadOnlyCnasDbContext _db;
    private readonly ISqidService _sqids;
    private readonly ISearchRowLevelFilter _rowFilter;

    /// <summary>Creates the service with its scoped collaborators.</summary>
    /// <param name="db">Read-only DB context routed to the streaming replica.</param>
    /// <param name="sqids">Sqid encoder used to project ids onto the wire payload.</param>
    /// <param name="rowFilter">Row-level scope filter applied to every per-domain projector.</param>
    public UnifiedDataSearchService(
        IReadOnlyCnasDbContext db,
        ISqidService sqids,
        ISearchRowLevelFilter rowFilter)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(rowFilter);
        _db = db;
        _sqids = sqids;
        _rowFilter = rowFilter;
    }

    /// <inheritdoc />
    public async Task<Result<UnifiedSearchResult>> SearchAsync(
        UnifiedSearchInput input,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(user);

        if (string.IsNullOrWhiteSpace(input.Query))
        {
            return Result<UnifiedSearchResult>.Failure(
                ErrorCodes.ValidationFailed,
                "Query is required.");
        }
        if (input.Query.Length > GlobalSearchInputValidator.MaxQueryLength)
        {
            return Result<UnifiedSearchResult>.Failure(
                ErrorCodes.ValidationFailed,
                $"Query must be at most {GlobalSearchInputValidator.MaxQueryLength} characters.");
        }

        var skip = Math.Max(0, input.Skip);
        var take = Math.Clamp(input.Take, 1, GlobalSearchInputValidator.MaxTake);
        var domains = NormaliseDomains(input.Domains);
        if (domains.Count == 0)
        {
            return Result<UnifiedSearchResult>.Failure(
                ErrorCodes.ValidationFailed,
                "At least one valid domain must be selected.");
        }

        var query = input.Query.Trim();
        var merged = new List<UnifiedSearchHitDto>();
        foreach (var domain in domains)
        {
            var hits = await SearchDomainAsync(domain, query, user, ct).ConfigureAwait(false);
            merged.AddRange(hits);
        }

        merged.Sort(static (a, b) => b.RelevanceScore.CompareTo(a.RelevanceScore));
        var totalHits = merged.Count;
        var paged = merged.Skip(skip).Take(take).ToList();
        return Result<UnifiedSearchResult>.Success(new UnifiedSearchResult(totalHits, paged, skip, take));
    }

    // ─────────────────────── per-domain dispatch ───────────────────────

    /// <summary>
    /// Dispatches the per-domain projector based on the canonical domain code.
    /// Unknown codes return an empty list (already filtered by
    /// <see cref="NormaliseDomains"/>).
    /// </summary>
    /// <param name="domain">Canonical lower-kebab-case domain code.</param>
    /// <param name="query">Trimmed user query.</param>
    /// <param name="user">Caller principal driving row-level scope.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The per-domain hit list (bounded by <see cref="PerDomainCap"/>).</returns>
    private Task<IReadOnlyList<UnifiedSearchHitDto>> SearchDomainAsync(
        string domain,
        string query,
        ClaimsPrincipal user,
        CancellationToken ct) => domain switch
        {
            GlobalSearchDomains.Applicants => ApplicantsAsync(query, user, ct),
            GlobalSearchDomains.Applications => ApplicationsAsync(query, user, ct),
            GlobalSearchDomains.Dossiers => DossiersAsync(query, user, ct),
            GlobalSearchDomains.Payers => PayersAsync(query, user, ct),
            GlobalSearchDomains.Contributors => PayersAsync(query, user, ct), // legacy alias
            GlobalSearchDomains.InsuredPersons => InsuredPersonsAsync(query, user, ct),
            GlobalSearchDomains.Tasks => TasksAsync(query, user, ct),
            GlobalSearchDomains.Notifications => NotificationsAsync(query, user, ct),
            GlobalSearchDomains.IssuedDocuments => DocumentsAsync(query, user, GlobalSearchDomains.IssuedDocuments, ct),
            GlobalSearchDomains.WorkflowDocuments => DocumentsAsync(query, user, GlobalSearchDomains.WorkflowDocuments, ct),
            GlobalSearchDomains.Documents => DocumentsAsync(query, user, GlobalSearchDomains.Documents, ct),
            _ => Task.FromResult<IReadOnlyList<UnifiedSearchHitDto>>(Array.Empty<UnifiedSearchHitDto>()),
        };

    // ─────────────────────── projectors ───────────────────────

    /// <summary>Applicants (Solicitanți) projector.</summary>
    /// <param name="query">Trimmed user query.</param>
    /// <param name="user">Caller principal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The unified hit list.</returns>
    private async Task<IReadOnlyList<UnifiedSearchHitDto>> ApplicantsAsync(
        string query, ClaimsPrincipal user, CancellationToken ct)
    {
        var src = _db.Solicitants.Where(s => s.IsActive
            && s.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
        src = _rowFilter.ApplyRowLevelScope(src, user, GlobalSearchDomains.Applicants);
        var rows = await src
            .Take(PerDomainCap)
            .Select(s => new { s.Id, s.DisplayName, s.Kind, s.RegionCode })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new UnifiedSearchHitDto(
            GlobalSearchDomains.Applicants,
            _sqids.Encode(r.Id),
            r.DisplayName,
            r.Kind.ToString(),
            r.DisplayName,
            $"/applicants/{_sqids.Encode(r.Id)}",
            ScoreOf(r.DisplayName, query),
            BuildHighlights(r.DisplayName, query))).ToList();
    }

    /// <summary>Applications (Cereri) projector.</summary>
    /// <param name="query">Trimmed user query.</param>
    /// <param name="user">Caller principal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The unified hit list.</returns>
    private async Task<IReadOnlyList<UnifiedSearchHitDto>> ApplicationsAsync(
        string query, ClaimsPrincipal user, CancellationToken ct)
    {
        var src = _db.Applications.Where(a => a.IsActive
            && a.ReferenceNumber != null
            && a.ReferenceNumber.Contains(query, StringComparison.OrdinalIgnoreCase));
        src = _rowFilter.ApplyRowLevelScope(src, user, GlobalSearchDomains.Applications);
        var rows = await src
            .Take(PerDomainCap)
            .Select(a => new { a.Id, a.ReferenceNumber, a.Status })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new UnifiedSearchHitDto(
            GlobalSearchDomains.Applications,
            _sqids.Encode(r.Id),
            r.ReferenceNumber ?? string.Empty,
            r.Status.ToString(),
            r.ReferenceNumber ?? string.Empty,
            $"/applications/{_sqids.Encode(r.Id)}",
            ScoreOf(r.ReferenceNumber, query),
            BuildHighlights(r.ReferenceNumber, query))).ToList();
    }

    /// <summary>Dossiers (Dosare) projector.</summary>
    /// <param name="query">Trimmed user query.</param>
    /// <param name="user">Caller principal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The unified hit list.</returns>
    private async Task<IReadOnlyList<UnifiedSearchHitDto>> DossiersAsync(
        string query, ClaimsPrincipal user, CancellationToken ct)
    {
        var src = _db.Dossiers.Where(d => d.IsActive
            && d.DossierNumber.Contains(query, StringComparison.OrdinalIgnoreCase));
        src = _rowFilter.ApplyRowLevelScope(src, user, GlobalSearchDomains.Dossiers);
        var rows = await src
            .Take(PerDomainCap)
            .Select(d => new { d.Id, d.DossierNumber })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new UnifiedSearchHitDto(
            GlobalSearchDomains.Dossiers,
            _sqids.Encode(r.Id),
            r.DossierNumber,
            string.Empty,
            r.DossierNumber,
            $"/dossiers/{_sqids.Encode(r.Id)}",
            ScoreOf(r.DossierNumber, query),
            BuildHighlights(r.DossierNumber, query))).ToList();
    }

    /// <summary>Payers / Contributors (Plătitori) projector.</summary>
    /// <param name="query">Trimmed user query.</param>
    /// <param name="user">Caller principal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The unified hit list.</returns>
    private async Task<IReadOnlyList<UnifiedSearchHitDto>> PayersAsync(
        string query, ClaimsPrincipal user, CancellationToken ct)
    {
        var src = _db.Contributors.Where(c => c.IsActive
            && (c.Denumire.Contains(query, StringComparison.OrdinalIgnoreCase)
                || c.Idno.Contains(query, StringComparison.OrdinalIgnoreCase)));
        src = _rowFilter.ApplyRowLevelScope(src, user, GlobalSearchDomains.Payers);
        var rows = await src
            .Take(PerDomainCap)
            .Select(c => new { c.Id, c.Denumire, c.Idno })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new UnifiedSearchHitDto(
            GlobalSearchDomains.Payers,
            _sqids.Encode(r.Id),
            r.Denumire,
            r.Idno,
            r.Denumire,
            $"/payers/{_sqids.Encode(r.Id)}",
            Math.Max(ScoreOf(r.Denumire, query), ScoreOf(r.Idno, query)),
            BuildHighlights(r.Denumire, query))).ToList();
    }

    /// <summary>Insured persons (Persoane asigurate) projector.</summary>
    /// <param name="query">Trimmed user query.</param>
    /// <param name="user">Caller principal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The unified hit list.</returns>
    private async Task<IReadOnlyList<UnifiedSearchHitDto>> InsuredPersonsAsync(
        string query, ClaimsPrincipal user, CancellationToken ct)
    {
        var src = _db.InsuredPersons.Where(p => p.IsActive
            && (p.LastName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.FirstName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.Idnp.Contains(query, StringComparison.OrdinalIgnoreCase)));
        src = _rowFilter.ApplyRowLevelScope(src, user, GlobalSearchDomains.InsuredPersons);
        var rows = await src
            .Take(PerDomainCap)
            .Select(p => new { p.Id, p.LastName, p.FirstName, p.Idnp })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r =>
        {
            var title = r.LastName + " " + r.FirstName;
            var score = Math.Max(
                Math.Max(ScoreOf(r.LastName, query), ScoreOf(r.FirstName, query)),
                ScoreOf(r.Idnp, query));
            return new UnifiedSearchHitDto(
                GlobalSearchDomains.InsuredPersons,
                _sqids.Encode(r.Id),
                title,
                r.Idnp,
                title,
                $"/insured/{_sqids.Encode(r.Id)}",
                score,
                BuildHighlights(title, query));
        }).ToList();
    }

    /// <summary>Workflow tasks (Sarcini) projector.</summary>
    /// <param name="query">Trimmed user query.</param>
    /// <param name="user">Caller principal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The unified hit list.</returns>
    private async Task<IReadOnlyList<UnifiedSearchHitDto>> TasksAsync(
        string query, ClaimsPrincipal user, CancellationToken ct)
    {
        var src = _db.WorkflowTasks.Where(t => t.IsActive
            && t.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
        src = _rowFilter.ApplyRowLevelScope(src, user, GlobalSearchDomains.Tasks);
        var rows = await src
            .Take(PerDomainCap)
            .Select(t => new { t.Id, t.Title, t.Status, t.DossierId })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new UnifiedSearchHitDto(
            GlobalSearchDomains.Tasks,
            _sqids.Encode(r.Id),
            r.Title,
            r.Status.ToString(),
            r.Title,
            $"/tasks/{_sqids.Encode(r.Id)}",
            ScoreOf(r.Title, query),
            BuildHighlights(r.Title, query))).ToList();
    }

    /// <summary>Notifications projector.</summary>
    /// <param name="query">Trimmed user query.</param>
    /// <param name="user">Caller principal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The unified hit list.</returns>
    private async Task<IReadOnlyList<UnifiedSearchHitDto>> NotificationsAsync(
        string query, ClaimsPrincipal user, CancellationToken ct)
    {
        var src = _db.Notifications.Where(n => n.IsActive
            && (n.Subject.Contains(query, StringComparison.OrdinalIgnoreCase)
                || n.Body.Contains(query, StringComparison.OrdinalIgnoreCase)));
        src = _rowFilter.ApplyRowLevelScope(src, user, GlobalSearchDomains.Notifications);
        var rows = await src
            .Take(PerDomainCap)
            .Select(n => new { n.Id, n.Subject, n.Body, n.Channel })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new UnifiedSearchHitDto(
            GlobalSearchDomains.Notifications,
            _sqids.Encode(r.Id),
            r.Subject,
            r.Channel.ToString(),
            Truncate(r.Body, 200),
            $"/notifications/{_sqids.Encode(r.Id)}",
            Math.Max(ScoreOf(r.Subject, query), ScoreOf(r.Body, query)),
            BuildHighlights(r.Subject, query))).ToList();
    }

    /// <summary>Documents projector (Issued / Workflow / generic). The supplied
    /// <paramref name="domain"/> code is forwarded to the hit so the UI renders
    /// the correct icon and route.</summary>
    /// <param name="query">Trimmed user query.</param>
    /// <param name="user">Caller principal.</param>
    /// <param name="domain">The unified domain code for the hits.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The unified hit list.</returns>
    private async Task<IReadOnlyList<UnifiedSearchHitDto>> DocumentsAsync(
        string query, ClaimsPrincipal user, string domain, CancellationToken ct)
    {
        var src = _db.Documents.Where(d => d.IsActive
            && (d.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (d.VerdictNote != null
                    && d.VerdictNote.Contains(query, StringComparison.OrdinalIgnoreCase))));
        src = _rowFilter.ApplyRowLevelScope(src, user, domain);
        var rows = await src
            .Take(PerDomainCap)
            .Select(d => new { d.Id, d.Title, d.VerdictNote, d.Kind })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new UnifiedSearchHitDto(
            domain,
            _sqids.Encode(r.Id),
            r.Title,
            r.Kind.ToString(),
            r.VerdictNote ?? r.Title,
            $"/documents/{_sqids.Encode(r.Id)}",
            Math.Max(ScoreOf(r.Title, query), ScoreOf(r.VerdictNote, query)),
            BuildHighlights(r.Title, query))).ToList();
    }

    // ─────────────────────── helpers ───────────────────────

    /// <summary>
    /// Computes a deterministic relevance proxy: occurrence count of the
    /// (case-folded) query inside the haystack divided by the haystack
    /// length. Matches the shape used by
    /// <c>PostgresGlobalSearchService.InMemoryRank</c> so the unified service
    /// orders comparably on the test fixture.
    /// </summary>
    /// <param name="haystack">The source text.</param>
    /// <param name="needle">The user query.</param>
    /// <returns>A non-negative relevance proxy (zero when no match).</returns>
    private static double ScoreOf(string? haystack, string needle)
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

    /// <summary>
    /// Builds a single-fragment highlight list: the original text with the
    /// match index marked via a fenced excerpt. Returns an empty list when
    /// the query does not occur — never returns null.
    /// </summary>
    /// <param name="text">Source string (nullable).</param>
    /// <param name="query">User query.</param>
    /// <returns>The highlight list (possibly empty).</returns>
    private static IReadOnlyList<string> BuildHighlights(string? text, string query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
        {
            return Array.Empty<string>();
        }
        var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return Array.Empty<string>();
        }
        var start = Math.Max(0, idx - 20);
        var end = Math.Min(text.Length, idx + query.Length + 20);
        var fragment = text.Substring(start, end - start);
        return new[] { fragment };
    }

    /// <summary>Truncates <paramref name="text"/> to <paramref name="max"/> chars with an ellipsis.</summary>
    /// <param name="text">Source text (nullable).</param>
    /// <param name="max">Maximum character length.</param>
    /// <returns>The truncated text (never null).</returns>
    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>
    /// Normalises the supplied domain filter into a case-folded, deduplicated,
    /// validation-filtered list. Null / empty input expands to every
    /// canonical unified domain.
    /// </summary>
    /// <param name="raw">User-supplied domain filter.</param>
    /// <returns>The validated, lowercased domain list.</returns>
    private static IReadOnlyList<string> NormaliseDomains(IReadOnlyList<string>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return GlobalSearchDomains.Unified;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(raw.Count);
        foreach (var entry in raw)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }
            foreach (var canonical in GlobalSearchDomains.Unified)
            {
                if (string.Equals(canonical, entry, StringComparison.OrdinalIgnoreCase)
                    && seen.Add(canonical))
                {
                    result.Add(canonical);
                }
            }
        }
        return result;
    }
}
