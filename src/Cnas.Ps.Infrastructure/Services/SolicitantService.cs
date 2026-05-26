using Cnas.Ps.Application.AccessScope;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Application.Solicitants;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Contracts.Search;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0167 — Solicitant registry list façade. Wires the
/// <see cref="IQueryBudgetService"/> guard onto the filter-applied queryable, then
/// materialises a single page when the budget allows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-request scope.</b> Registered as <c>Scoped</c> so the per-instance
/// <see cref="LastBudgetVerdict"/> slot can carry the most-recent verdict back to the
/// controller without inventing a side-channel. Each HTTP request gets a fresh
/// instance, so the slot is implicitly request-scoped.
/// </para>
/// <para>
/// <b>Sqid invariant.</b> Sqid encoding happens in-process AFTER materialisation;
/// <see cref="ISqidService"/> is not translatable to SQL.
/// </para>
/// </remarks>
/// <param name="db">EF Core context abstraction.</param>
/// <param name="sqids">Sqid encoder used for the projected list-item ids.</param>
/// <param name="budget">Query-budget guard consulted before materialisation.</param>
/// <param name="qbeConverter">
/// R0163 — Query-By-Example LINQ converter. Used by <see cref="SearchAsync"/> to splice a
/// typed predicate onto the filtered queryable BEFORE the budget guard is consulted.
/// </param>
/// <param name="suggestions">
/// R0525 — search-suggestion service consulted on every successful list call. Emits
/// advisory <see cref="SearchSuggestionDto"/> rows when the result set is over the
/// refinement threshold; cached on <see cref="LastSuggestions"/> for the controller
/// to surface in the wire response.
/// </param>
/// <param name="accessScopeFilter">
/// R0671 / TOR CF 18.06 — row-level access-scope predicate splicer. Applied BEFORE the
/// budget gate so the budget evaluates the SCOPED row count, not the unscoped one
/// (which is a security property: an unscoped caller cannot bypass the per-request
/// limit by being "lucky" with a permissive QBE predicate).
/// </param>
/// <param name="caller">
/// R0671 — request-scoped caller context supplying the
/// <see cref="ICallerContext.AccessScope"/> envelope consumed by
/// <paramref name="accessScopeFilter"/>.
/// </param>
/// <param name="referenceGuard">
/// R0623 / TOR CF 13.04 — OPEN-state foreign-reference scanner consulted by
/// <see cref="DeactivateAsync"/> before a soft-delete is allowed.
/// </param>
/// <param name="clock">
/// UTC clock — stamps <c>Solicitant.UpdatedAtUtc</c> on deactivation; never
/// <see cref="DateTime.UtcNow"/> directly (CLAUDE.md "UTC Everywhere").
/// </param>
public sealed class SolicitantService(
    ICnasDbContext db,
    ISqidService sqids,
    IQueryBudgetService budget,
    IQbeToLinqConverter qbeConverter,
    ISearchSuggestionService suggestions,
    IAccessScopeFilter accessScopeFilter,
    ICallerContext caller,
    ISolicitantReferenceGuard referenceGuard,
    ICnasTimeProvider clock) : ISolicitantService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly IQueryBudgetService _budget = budget;
    private readonly IQbeToLinqConverter _qbeConverter = qbeConverter;
    private readonly ISearchSuggestionService _suggestions = suggestions;
    private readonly IAccessScopeFilter _accessScopeFilter = accessScopeFilter;
    private readonly ICallerContext _caller = caller;
    private readonly ISolicitantReferenceGuard _referenceGuard = referenceGuard;
    private readonly ICnasTimeProvider _clock = clock;

    /// <inheritdoc />
    public QueryBudgetVerdict? LastBudgetVerdict { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<SearchSuggestionDto>? LastSuggestions { get; private set; }

    /// <inheritdoc />
    public Task<Result<PagedResult<SolicitantListItem>>> SearchAsync(
        SolicitantListQueryInput input,
        QbeFilter? qbe,
        CancellationToken ct = default) => ListInternalAsync(input, qbe, ct);

    /// <inheritdoc />
    public Task<Result<PagedResult<SolicitantListItem>>> ListAsync(
        SolicitantListQueryInput input,
        CancellationToken ct = default) => ListInternalAsync(input, qbe: null, ct);

    /// <summary>
    /// Shared list pipeline consumed by both <see cref="ListAsync"/> and
    /// <see cref="SearchAsync"/>. Builds the filtered queryable, optionally splices the
    /// QBE predicate, consults the budget guard, then materialises a page.
    /// </summary>
    /// <param name="input">Query input (free-text + date range + paging).</param>
    /// <param name="qbe">Optional QBE filter envelope; null = no QBE narrowing applied.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Paged result, or a <see cref="Result{T}.Failure"/> with a stable error code.</returns>
    private async Task<Result<PagedResult<SolicitantListItem>>> ListInternalAsync(
        SolicitantListQueryInput input,
        QbeFilter? qbe,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Reset the verdict + suggestions slots on each call so stale state from a
        // previous list call on the same service instance cannot leak into a fresh
        // response.
        LastBudgetVerdict = null;
        LastSuggestions = null;

        var pageSize = Math.Clamp(input.PageSize, 1, 200);
        var pageNumber = Math.Max(1, input.Page);
        var skip = (pageNumber - 1) * pageSize;

        // 1. Build the filtered queryable. Only NON-DEFAULT filter values become DB
        //    predicates AND become entries in the QueryFilterContext — so the budget
        //    guard's hint rules see exactly the shape of filtering the caller asked
        //    for.
        IQueryable<Solicitant> query = _db.Solicitants.Where(s => s.IsActive);
        // R0671 / CF 18.06 — splice the access-scope predicate FIRST. The filter is
        // a no-op for unscoped callers; for scoped callers it narrows the queryable
        // to the regions they may see BEFORE the budget guard counts rows. Running
        // before QBE / Q / date filters also guarantees the budget verdict reflects
        // the SCOPED count, not the unscoped one — the security property documented
        // on IAccessScopeFilter.
        query = _accessScopeFilter.ApplyToSolicitants(query, _caller.AccessScope);
        var ctxBuilder = new QueryFilterContext();

        // R0163 — splice the QBE predicate FIRST (before the bespoke filter fields below
        // get applied) so an over-filtered QBE call still consumes the same budget guard
        // path as the legacy GET endpoint. The converter validates the envelope and
        // returns a stable QBE_* error code on malformed input — surface those verbatim
        // so the controller can render a field-targeted ProblemDetails.
        if (qbe is not null && qbe.Conditions.Count > 0)
        {
            var predicateResult = _qbeConverter.Convert<Solicitant>(
                QueryBudgetRegistries.Solicitant, qbe);
            if (predicateResult.IsFailure)
            {
                return Result<PagedResult<SolicitantListItem>>.Failure(
                    predicateResult.ErrorCode!, predicateResult.ErrorMessage!);
            }
            query = query.Where(predicateResult.Value);
            // Record a synthetic context entry so the budget hint rules can recognise
            // that QBE narrowed the query — prevents a "you must add a free-text filter"
            // hint from firing when the caller already filtered on Email exact match.
            ctxBuilder = ctxBuilder.With("Qbe", qbe.Conditions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(input.Q))
        {
            var trimmed = input.Q.Trim();
            ctxBuilder = ctxBuilder.With("Q", trimmed);
            var folded = DiacriticFolding.Fold(trimmed);
            if (IsRelationalProvider(_db))
            {
                var likePattern = WildcardMask.ToLikePattern(folded);
                query = query.Where(s =>
                    EF.Functions.ILike(CnasDbFunctions.Unaccent(s.DisplayName), likePattern));
            }
            else
            {
                var regex = WildcardMask.ToRegex(folded);
                query = query.Where(s => regex.IsMatch(DiacriticFolding.Fold(s.DisplayName)));
            }
        }

        if (input.CreatedFromUtc is { } from)
        {
            ctxBuilder = ctxBuilder.With("CreatedFromUtc", from);
            query = query.Where(s => s.CreatedAtUtc >= from);
        }

        if (input.CreatedToUtc is { } to)
        {
            ctxBuilder = ctxBuilder.With("CreatedToUtc", to);
            query = query.Where(s => s.CreatedAtUtc < to);
        }

        // 2. Consult the budget guard BEFORE materialising. The verdict is cached on
        //    LastBudgetVerdict so the controller can populate the ProblemDetails
        //    extensions bag whether the verdict allowed or refused.
        var verdict = await _budget.EvaluateAsync(
            QueryBudgetRegistries.Solicitant,
            query,
            ctxBuilder,
            ct).ConfigureAwait(false);
        LastBudgetVerdict = verdict;

        if (!verdict.Allowed)
        {
            return Result<PagedResult<SolicitantListItem>>.Failure(
                ErrorCodes.QueryTooBroad,
                QueryBudgetFailureEnvelope.FailureMessage);
        }

        // 3. Materialise the page. The total count from the verdict can be re-used to
        //    populate the PagedResult.TotalCount slot, saving a second COUNT round-
        //    trip when the page lives near the start of the result set.
        // R0523 / CF 03.05 — honour the caller's user-defined ordering when supplied.
        // Otherwise fall back to the canonical default (DisplayName ASC, Id ASC).
        IQueryable<Solicitant> ordered;
        if (qbe is { Orderings: { Count: > 0 } orderings })
        {
            var orderResult = _qbeConverter.ApplyOrdering<Solicitant>(
                query, QueryBudgetRegistries.Solicitant, orderings);
            if (orderResult.IsFailure)
            {
                return Result<PagedResult<SolicitantListItem>>.Failure(
                    orderResult.ErrorCode!, orderResult.ErrorMessage!);
            }
            ordered = orderResult.Value;
        }
        else
        {
            ordered = query.OrderBy(s => s.DisplayName).ThenBy(s => s.Id);
        }

        var rows = await ordered
            .Skip(skip).Take(pageSize)
            .Select(s => new
            {
                s.Id,
                s.DisplayName,
                s.Kind,
                s.CreatedAtUtc,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var items = rows
            .Select(r => new SolicitantListItem(
                _sqids.Encode(r.Id),
                r.DisplayName,
                r.Kind.ToString(),
                r.CreatedAtUtc))
            .ToList();

        // R0525 / CF 03.08 — consult the suggestion service when the result-set count
        // is over the refinement threshold. The service returns an empty list when no
        // suggestion applies; cache whatever it returns on LastSuggestions so the
        // controller can surface it (or its absence) deterministically.
        var suggestions = await _suggestions.SuggestRefinementsAsync(
            QueryBudgetRegistries.Solicitant,
            qbe,
            verdict.EstimatedRowCount,
            ct).ConfigureAwait(false);
        if (suggestions.IsSuccess)
        {
            LastSuggestions = suggestions.Value;
        }

        return Result<PagedResult<SolicitantListItem>>.Success(
            new PagedResult<SolicitantListItem>(items, pageNumber, pageSize, verdict.EstimatedRowCount));
    }

    /// <inheritdoc />
    public Task<Result<SolicitantReferenceScanDto>> ScanReferencesAsync(
        string solicitantSqid,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solicitantSqid);
        // Thin pass-through to the injected guard — keeps the policy decision
        // (block / allow) on this service and the COUNT primitives on the guard.
        return _referenceGuard.ScanAsync(solicitantSqid, ct);
    }

    /// <inheritdoc />
    public async Task<Result> DeactivateAsync(
        string solicitantSqid,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solicitantSqid);

        // Sqid decode at the boundary; the rest of the method works on the raw long.
        var decoded = _sqids.TryDecode(solicitantSqid);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var solicitantId = decoded.Value;

        // Locate the row first so we can return a precise NotFound and avoid
        // a wasted scan against a non-existent id.
        var existing = await _db.Solicitants
            .SingleOrDefaultAsync(s => s.Id == solicitantId, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return Result.Failure(
                ErrorCodes.NotFound,
                $"Solicitant '{solicitantSqid}' was not found.");
        }
        if (!existing.IsActive)
        {
            // Idempotent: a second deactivate is a no-op success — keeps the
            // admin UI safe against double-clicks and replay.
            return Result.Success();
        }

        // R0623 / TOR CF 13.04 — pre-flight reference-block. The guard counts
        // OPEN-state references only; closed / terminal-state rows are
        // intentionally excluded so historical artefacts never strand a
        // deactivation.
        var scan = await _referenceGuard.ScanAsync(solicitantSqid, ct).ConfigureAwait(false);
        if (scan.IsFailure)
        {
            // The guard's own NotFound / InvalidSqid path; surface verbatim.
            return Result.Failure(scan.ErrorCode!, scan.ErrorMessage!);
        }
        if (scan.Value.TotalOpen > 0)
        {
            var v = scan.Value;
            // Compose a per-table breakdown into the failure message so the admin
            // UI can render a precise prompt without re-issuing the scan call.
            // The format is stable (`apps=N, dossiers=N, ...`) so a test can pin
            // the exact wire shape without parsing free text.
            return Result.Failure(
                ErrorCodes.SolicitantReferencedByOpenRecords,
                $"Solicitant '{solicitantSqid}' is referenced by {v.TotalOpen} open record(s) "
                    + $"(apps={v.ApplicationsOpen}, dossiers={v.DossiersOpen}, "
                    + $"documents={v.DocumentsOpen}, payments={v.PaymentsOpen}, "
                    + $"notifications={v.NotificationsOpen}) and cannot be deactivated.");
        }

        // All clear — soft-delete and stamp the modification timestamp.
        existing.IsActive = false;
        existing.UpdatedAtUtc = _clock.UtcNow;
        existing.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>
    /// Detects whether the underlying <see cref="ICnasDbContext"/> is backed by a relational
    /// provider (Npgsql in production) vs the in-memory test fake. Mirrors the seam from
    /// <see cref="InsuredPersonService"/>.
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
}
