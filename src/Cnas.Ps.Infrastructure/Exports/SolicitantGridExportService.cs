using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Exports;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Exports;

/// <summary>
/// R0226 / TOR UI 013 — production wiring of the universal grid-export pipeline
/// for the Solicitant registry. Accepts the same
/// <see cref="SolicitantListQueryInput"/> as
/// <c>SolicitantService.ListAsync</c>, runs it through the same
/// <see cref="IQueryBudgetService"/> gate, projects the rows via
/// <see cref="SolicitantGridAdapter"/>, and delegates the byte-level rendering
/// to <see cref="IGridExporter"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-request scope.</b> Mirrors <c>SolicitantService</c>'s lifetime so the
/// <see cref="LastBudgetVerdict"/> slot is implicitly request-scoped.
/// </para>
/// <para>
/// <b>PII gate.</b> The Solicitant registry's display-name column is masked
/// unless the caller holds an admin / decider role. The role check is the
/// stand-in for an explicit <c>Solicitant.ViewPii</c> permission — no such
/// permission constant exists in the codebase today (CLAUDE.md tracker has
/// "default to redacting"). When the call comes from an unauthenticated
/// context (<see cref="ICallerContext.Roles"/> is empty), the adapter
/// masks — the safer default.
/// </para>
/// </remarks>
public sealed class SolicitantGridExportService : ISolicitantGridExportService
{
    /// <summary>EF Core context abstraction.</summary>
    private readonly ICnasDbContext _db;

    /// <summary>Query-budget guard.</summary>
    private readonly IQueryBudgetService _budget;

    /// <summary>Sqid encoder.</summary>
    private readonly ISqidService _sqids;

    /// <summary>Universal exporter façade.</summary>
    private readonly IGridExporter _exporter;

    /// <summary>Solicitant-specific adapter.</summary>
    private readonly SolicitantGridAdapter _adapter;

    /// <summary>Caller-context accessor used to decide whether to redact PII.</summary>
    private readonly ICallerContext _caller;

    /// <summary>Creates a new <see cref="SolicitantGridExportService"/>.</summary>
    /// <param name="db">EF Core context abstraction.</param>
    /// <param name="budget">Query-budget guard.</param>
    /// <param name="sqids">Sqid encoder.</param>
    /// <param name="exporter">Universal exporter façade.</param>
    /// <param name="adapter">
    /// Solicitant grid adapter. Stateless and reusable — registered as a
    /// singleton.
    /// </param>
    /// <param name="caller">Caller-context accessor for the PII gate.</param>
    public SolicitantGridExportService(
        ICnasDbContext db,
        IQueryBudgetService budget,
        ISqidService sqids,
        IGridExporter exporter,
        SolicitantGridAdapter adapter,
        ICallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(caller);

        _db = db;
        _budget = budget;
        _sqids = sqids;
        _exporter = exporter;
        _adapter = adapter;
        _caller = caller;
    }

    /// <inheritdoc />
    public QueryBudgetVerdict? LastBudgetVerdict { get; private set; }

    /// <inheritdoc />
    public async Task<Result<GridExportResult>> ExportAsync(
        SolicitantListQueryInput input,
        ExportFormat format,
        string? language = "ro",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        LastBudgetVerdict = null;

        // 1. Build the same filtered queryable used by SolicitantService.ListAsync.
        IQueryable<Solicitant> query = _db.Solicitants.Where(s => s.IsActive);
        var ctxBuilder = new QueryFilterContext();

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

        // 2. Budget guard — refuse over-budget exports BEFORE materialising.
        var verdict = await _budget.EvaluateAsync(
            QueryBudgetRegistries.Solicitant,
            query,
            ctxBuilder,
            ct).ConfigureAwait(false);
        LastBudgetVerdict = verdict;

        if (!verdict.Allowed)
        {
            return Result<GridExportResult>.Failure(
                ErrorCodes.QueryTooBroad,
                QueryBudgetFailureEnvelope.FailureMessage);
        }

        // 3. Materialise the whole filtered set — the budget guard already
        //    capped it. Use a flat anonymous projection so EF only pulls the
        //    columns we render (no encrypted-column overhead).
        var ordered = query.OrderBy(s => s.DisplayName).ThenBy(s => s.Id);
        var rows = await ordered
            .Select(s => new SolicitantGridRow(
                s.Id,
                s.NationalIdHash,
                s.DisplayName,
                s.Kind.ToString(),
                s.CreatedAtUtc,
                s.IsActive))
            .ToListAsync(ct).ConfigureAwait(false);

        // 4. Adapt to GridRow + decide on PII redaction. Baseline cnas-user
        //    roles get masked names; cnas-decider / cnas-admin see the full
        //    display name. Anonymous (empty roles) falls back to masked.
        var canViewPii = CanViewPii(_caller);
        var gridRows = rows.Select(r => _adapter.ToRow(r, _sqids, canViewPii)).ToList();
        var columns = _adapter.Columns(language ?? "ro");
        var request = new GridExportRequest(
            GridName: SolicitantsGridName,
            Columns: columns,
            Rows: gridRows,
            Title: SolicitantsGridName,
            FooterNote: null,
            Language: language ?? "ro");

        return await _exporter.ExportAsync(request, format, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stable grid name used as the file-name prefix and the
    /// <c>cnas.grid_export.requested{grid}</c> metric tag.
    /// </summary>
    internal const string SolicitantsGridName = "Solicitants";

    /// <summary>
    /// Decides whether the caller may see PII on Solicitant exports. The
    /// codebase has no explicit <c>Solicitant.ViewPii</c> permission today, so
    /// we treat the <c>cnas-decider</c> and <c>cnas-admin</c> roles as the
    /// equivalent grant. Baseline <c>cnas-user</c> + unauthenticated callers
    /// see the masked name.
    /// </summary>
    /// <param name="caller">Caller context.</param>
    /// <returns><c>true</c> when the caller may see unmasked PII.</returns>
    internal static bool CanViewPii(ICallerContext caller)
    {
        if (caller.Roles is null || caller.Roles.Count == 0)
        {
            return false;
        }
        foreach (var role in caller.Roles)
        {
            if (string.Equals(role, "cnas-admin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "cnas-decider", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Detects whether the underlying <see cref="ICnasDbContext"/> is backed
    /// by a relational provider. Mirrors <c>SolicitantService</c>.
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
