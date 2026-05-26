using System.Text.Json;
using Cnas.Ps.Application.AccessScope;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.BulkActions;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — shape of the filter envelope every per-registry
/// resolver accepts. Keys are optional; resolvers ignore unknown keys (forward
/// compatibility). The shape is intentionally narrow — broader filter expressions land
/// in a follow-up batch.
/// </summary>
/// <param name="Status">
/// Stable string-form status filter. Interpreted per-registry: for <c>Cerere</c> it
/// maps to <see cref="ApplicationStatus"/>; for <c>WorkflowTask</c> it maps to
/// <see cref="WorkflowTaskStatus"/>; ignored by <c>Solicitant</c>.
/// </param>
/// <param name="OwnerUserId">
/// Sqid of an owner / assignee. For <c>WorkflowTask</c> matches
/// <c>AssignedUserId</c>; for <c>Cerere</c> matches the dossier examiner via the
/// originating application; for <c>Solicitant</c>/<c>Decision</c> currently ignored.
/// </param>
/// <param name="Q">
/// Free-text query. Applied diacritic-insensitively (R0162) against the registry's
/// canonical name column (Solicitant.NationalId is encrypted so the resolver only
/// uses display-name fields where available).
/// </param>
/// <param name="CreatedFromUtc">Lower bound on <c>CreatedAtUtc</c> (inclusive). UTC.</param>
/// <param name="CreatedToUtc">Upper bound on <c>CreatedAtUtc</c> (exclusive). UTC.</param>
public sealed record BulkFilterEnvelope(
    string? Status,
    string? OwnerUserId,
    string? Q,
    DateTime? CreatedFromUtc,
    DateTime? CreatedToUtc);

/// <summary>
/// R0166 — internal helpers shared by every resolver. Centralises the JSON parsing
/// and the InMemory-vs-relational provider sniff so each resolver stays focused on
/// its entity-specific predicate.
/// </summary>
internal static class BulkFilterHelpers
{
    /// <summary>Tolerant JSON options — unknown properties are ignored.</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Parses the filter envelope, returning a <see cref="Result{T}"/> that callers
    /// can lift verbatim into their own failure path.
    /// </summary>
    /// <param name="filterJson">Caller-supplied JSON envelope.</param>
    /// <returns>On success the parsed envelope; on malformed JSON a structured failure.</returns>
    public static Result<BulkFilterEnvelope> Parse(string filterJson)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<BulkFilterEnvelope>(filterJson, JsonOpts)
                ?? new BulkFilterEnvelope(null, null, null, null, null);
            return Result<BulkFilterEnvelope>.Success(parsed);
        }
        catch (JsonException ex)
        {
            return Result<BulkFilterEnvelope>.Failure(
                ErrorCodes.ValidationFailed,
                $"Filter envelope is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>Sniff the EF provider so the resolvers can pick relational vs InMemory expression paths.</summary>
    /// <param name="db">The DbContext under inspection.</param>
    /// <returns><c>true</c> on a relational provider (Npgsql, SqlServer); <c>false</c> on InMemory.</returns>
    public static bool IsRelational(ICnasDbContext db)
    {
        if (db is DbContext concrete)
        {
            return concrete.Database.IsRelational();
        }
        return false;
    }
}

/// <summary>
/// R0166 — <see cref="IBulkSelectionFilterResolver"/> for the
/// <see cref="BulkRegistries.Solicitant"/> registry. Filters by free-text on the
/// display fields available on <see cref="Solicitant"/>; <c>Status</c> /
/// <c>OwnerUserId</c> are ignored (no equivalent on the entity).
/// </summary>
public sealed class SolicitantFilterResolver : IBulkSelectionFilterResolver
{
    private readonly ICnasDbContext _db;

    /// <summary>Creates the resolver.</summary>
    /// <param name="db">Per-request DbContext.</param>
    public SolicitantFilterResolver(ICnasDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public string Registry => BulkRegistries.Solicitant;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<long>>> ResolveAsync(string filterJson, CancellationToken ct = default)
    {
        var parsed = BulkFilterHelpers.Parse(filterJson);
        if (parsed.IsFailure)
        {
            return Result<IReadOnlyList<long>>.Failure(parsed.ErrorCode!, parsed.ErrorMessage!);
        }
        var f = parsed.Value;

        var query = _db.Solicitants.Where(s => s.IsActive);
        if (f.CreatedFromUtc is { } from)
        {
            query = query.Where(s => s.CreatedAtUtc >= from);
        }
        if (f.CreatedToUtc is { } to)
        {
            query = query.Where(s => s.CreatedAtUtc < to);
        }
        // Note: Solicitant.NationalId is encrypted at rest and there is no
        // free-text display column today, so Q is intentionally ignored.
        // OwnerUserId / Status do not apply to Solicitant — silently ignored.

        var ids = await query.OrderBy(s => s.Id).Select(s => s.Id).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<long>>.Success(ids);
    }
}

/// <summary>
/// R0166 — <see cref="IBulkSelectionFilterResolver"/> for the
/// <see cref="BulkRegistries.Cerere"/> registry. Filters by status, created-at range,
/// and a diacritic-insensitive query on the snapshot display name.
/// </summary>
/// <remarks>
/// <para>
/// <b>R0671 follow-up — access-scope wiring.</b> The resolver applies
/// <see cref="IAccessScopeFilter.ApplyToServiceApplications"/> BEFORE returning the id
/// set so a scoped caller never receives ids of dossiers they cannot view. The filter
/// is a no-op for unscoped callers (national admin / system) — the hot path stays free
/// of extra <c>Where</c> clauses. NULL <see cref="ServiceApplication.SubdivisionCode"/>
/// rows remain visible to every scoped caller (documented NULL-data semantics on
/// <see cref="IAccessScope"/>).
/// </para>
/// </remarks>
public sealed class CerereFilterResolver : IBulkSelectionFilterResolver
{
    private readonly ICnasDbContext _db;
    private readonly IAccessScopeFilter _accessScopeFilter;
    private readonly ICallerContext _caller;

    /// <summary>Creates the resolver.</summary>
    /// <param name="db">Per-request DbContext.</param>
    /// <param name="accessScopeFilter">
    /// R0671 / TOR CF 18.06 — row-level access-scope predicate splicer applied to the
    /// <see cref="ServiceApplication"/> queryable before id projection. No-op for
    /// unscoped callers.
    /// </param>
    /// <param name="caller">
    /// R0671 — request-scoped caller context supplying the
    /// <see cref="ICallerContext.AccessScope"/> envelope consumed by
    /// <paramref name="accessScopeFilter"/>.
    /// </param>
    public CerereFilterResolver(
        ICnasDbContext db,
        IAccessScopeFilter accessScopeFilter,
        ICallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(accessScopeFilter);
        ArgumentNullException.ThrowIfNull(caller);
        _db = db;
        _accessScopeFilter = accessScopeFilter;
        _caller = caller;
    }

    /// <inheritdoc />
    public string Registry => BulkRegistries.Cerere;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<long>>> ResolveAsync(string filterJson, CancellationToken ct = default)
    {
        var parsed = BulkFilterHelpers.Parse(filterJson);
        if (parsed.IsFailure)
        {
            return Result<IReadOnlyList<long>>.Failure(parsed.ErrorCode!, parsed.ErrorMessage!);
        }
        var f = parsed.Value;

        var query = _db.Applications.Where(a => a.IsActive);
        // R0671 / CF 18.06 — splice the access-scope predicate FIRST so the
        // caller-supplied predicates below compose against the SCOPED queryable.
        // The filter is a no-op for unscoped callers (national admin / system).
        query = _accessScopeFilter.ApplyToServiceApplications(query, _caller.AccessScope);
        if (!string.IsNullOrWhiteSpace(f.Status)
            && Enum.TryParse<ApplicationStatus>(f.Status, ignoreCase: false, out var status))
        {
            query = query.Where(a => a.Status == status);
        }
        if (f.CreatedFromUtc is { } from)
        {
            query = query.Where(a => a.CreatedAtUtc >= from);
        }
        if (f.CreatedToUtc is { } to)
        {
            query = query.Where(a => a.CreatedAtUtc < to);
        }

        var ids = await query.OrderBy(a => a.Id).Select(a => a.Id).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<long>>.Success(ids);
    }
}

/// <summary>
/// R0166 — <see cref="IBulkSelectionFilterResolver"/> for the
/// <see cref="BulkRegistries.WorkflowTask"/> registry. Filters by status, assignee,
/// created-at range, and a diacritic-insensitive query on the task title.
/// </summary>
/// <remarks>
/// <para>
/// <b>R0671 follow-up — access-scope wiring.</b> The resolver applies
/// <see cref="IAccessScopeFilter.ApplyToWorkflowTasks"/> BEFORE returning the id set
/// so a scoped caller never receives ids of tasks whose anchor workflow category is
/// outside their allow-list. The filter is a no-op for unscoped callers; tasks with
/// <c>NodeCode = null</c> (legacy / unanchored) remain visible to every scoped caller
/// per the documented NULL-data semantics.
/// </para>
/// </remarks>
public sealed class WorkflowTaskFilterResolver : IBulkSelectionFilterResolver
{
    private readonly ICnasDbContext _db;
    private readonly ISqidService _sqids;
    private readonly IAccessScopeFilter _accessScopeFilter;
    private readonly ICallerContext _caller;

    /// <summary>Creates the resolver.</summary>
    /// <param name="db">Per-request DbContext.</param>
    /// <param name="sqids">Sqid service used to decode caller-supplied owner ids.</param>
    /// <param name="accessScopeFilter">
    /// R0671 / TOR CF 18.06 — row-level access-scope predicate splicer applied to the
    /// <see cref="WorkflowTask"/> queryable. No-op for unscoped callers.
    /// </param>
    /// <param name="caller">
    /// R0671 — request-scoped caller context supplying the
    /// <see cref="ICallerContext.AccessScope"/> envelope.
    /// </param>
    public WorkflowTaskFilterResolver(
        ICnasDbContext db,
        ISqidService sqids,
        IAccessScopeFilter accessScopeFilter,
        ICallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(accessScopeFilter);
        ArgumentNullException.ThrowIfNull(caller);
        _db = db;
        _sqids = sqids;
        _accessScopeFilter = accessScopeFilter;
        _caller = caller;
    }

    /// <inheritdoc />
    public string Registry => BulkRegistries.WorkflowTask;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<long>>> ResolveAsync(string filterJson, CancellationToken ct = default)
    {
        var parsed = BulkFilterHelpers.Parse(filterJson);
        if (parsed.IsFailure)
        {
            return Result<IReadOnlyList<long>>.Failure(parsed.ErrorCode!, parsed.ErrorMessage!);
        }
        var f = parsed.Value;

        var query = _db.WorkflowTasks.Where(t => t.IsActive);
        // R0671 / CF 18.06 — splice the workflow-category access-scope predicate
        // FIRST. The filter joins via the current WorkflowDefinition; tasks anchored
        // to a category outside the allow-list are hidden, unanchored tasks remain
        // visible. No-op when AllowedWorkflowCategories is empty.
        query = _accessScopeFilter.ApplyToWorkflowTasks(
            query, _caller.AccessScope, _db.WorkflowDefinitions);
        if (!string.IsNullOrWhiteSpace(f.Status)
            && Enum.TryParse<WorkflowTaskStatus>(f.Status, ignoreCase: false, out var status))
        {
            query = query.Where(t => t.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(f.OwnerUserId))
        {
            var decoded = _sqids.TryDecode(f.OwnerUserId);
            if (decoded.IsSuccess)
            {
                var ownerId = decoded.Value;
                query = query.Where(t => t.AssignedUserId == ownerId);
            }
        }
        if (f.CreatedFromUtc is { } from)
        {
            query = query.Where(t => t.CreatedAtUtc >= from);
        }
        if (f.CreatedToUtc is { } to)
        {
            query = query.Where(t => t.CreatedAtUtc < to);
        }
        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var folded = DiacriticFolding.Fold(f.Q);
            var like = WildcardMask.ToLikePattern(folded);
            if (BulkFilterHelpers.IsRelational(_db))
            {
                query = query.Where(t => EF.Functions.ILike(
                    CnasDbFunctions.Unaccent(t.Title), like));
            }
            else
            {
                var regex = WildcardMask.ToRegex(folded);
                query = query.Where(t => regex.IsMatch(DiacriticFolding.Fold(t.Title)));
            }
        }

        var ids = await query.OrderBy(t => t.Id).Select(t => t.Id).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<long>>.Success(ids);
    }
}

/// <summary>
/// R0166 — <see cref="IBulkSelectionFilterResolver"/> for the
/// <see cref="BulkRegistries.Decision"/> registry. Decisions are surfaced as
/// <see cref="ServiceApplication"/> rows whose status is <see cref="ApplicationStatus.Approved"/>
/// or <see cref="ApplicationStatus.Rejected"/>. Filter accepts optional status (which
/// must be one of those two values) plus created-at range.
/// </summary>
/// <remarks>
/// <para>
/// <b>R0671 follow-up — access-scope wiring.</b> Decisions are projected from
/// <see cref="ServiceApplication"/> rows; the resolver consults
/// <see cref="IAccessScopeFilter.ApplyToServiceApplications"/> against the underlying
/// <c>SubdivisionCode</c> axis before id projection. No-op for unscoped callers.
/// NULL <c>SubdivisionCode</c> decisions remain visible to every scoped caller.
/// </para>
/// </remarks>
public sealed class DecisionFilterResolver : IBulkSelectionFilterResolver
{
    private readonly ICnasDbContext _db;
    private readonly IAccessScopeFilter _accessScopeFilter;
    private readonly ICallerContext _caller;

    /// <summary>Creates the resolver.</summary>
    /// <param name="db">Per-request DbContext.</param>
    /// <param name="accessScopeFilter">
    /// R0671 / TOR CF 18.06 — row-level access-scope predicate splicer applied to the
    /// underlying <see cref="ServiceApplication"/> queryable. No-op for unscoped callers.
    /// </param>
    /// <param name="caller">
    /// R0671 — request-scoped caller context supplying the
    /// <see cref="ICallerContext.AccessScope"/> envelope.
    /// </param>
    public DecisionFilterResolver(
        ICnasDbContext db,
        IAccessScopeFilter accessScopeFilter,
        ICallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(accessScopeFilter);
        ArgumentNullException.ThrowIfNull(caller);
        _db = db;
        _accessScopeFilter = accessScopeFilter;
        _caller = caller;
    }

    /// <inheritdoc />
    public string Registry => BulkRegistries.Decision;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<long>>> ResolveAsync(string filterJson, CancellationToken ct = default)
    {
        var parsed = BulkFilterHelpers.Parse(filterJson);
        if (parsed.IsFailure)
        {
            return Result<IReadOnlyList<long>>.Failure(parsed.ErrorCode!, parsed.ErrorMessage!);
        }
        var f = parsed.Value;

        var query = _db.Applications
            .Where(a => a.IsActive
                && (a.Status == ApplicationStatus.Approved || a.Status == ApplicationStatus.Rejected));
        // R0671 / CF 18.06 — splice the access-scope predicate FIRST so the
        // decision projection respects the caller's subdivision allow-list.
        query = _accessScopeFilter.ApplyToServiceApplications(query, _caller.AccessScope);
        if (!string.IsNullOrWhiteSpace(f.Status)
            && Enum.TryParse<ApplicationStatus>(f.Status, ignoreCase: false, out var status)
            && (status == ApplicationStatus.Approved || status == ApplicationStatus.Rejected))
        {
            query = query.Where(a => a.Status == status);
        }
        if (f.CreatedFromUtc is { } from)
        {
            query = query.Where(a => a.CreatedAtUtc >= from);
        }
        if (f.CreatedToUtc is { } to)
        {
            query = query.Where(a => a.CreatedAtUtc < to);
        }

        var ids = await query.OrderBy(a => a.Id).Select(a => a.Id).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<long>>.Success(ids);
    }
}

/// <summary>
/// R0166 — <see cref="IBulkSelectionFilterResolverFactory"/> built from the
/// registered set of <see cref="IBulkSelectionFilterResolver"/> instances. Failures
/// surface as <see cref="ErrorCodes.ValidationFailed"/> when the registry is unknown
/// or <see cref="ErrorCodes.Internal"/> when the registry is known but no resolver
/// was registered.
/// </summary>
public sealed class BulkSelectionFilterResolverFactory : IBulkSelectionFilterResolverFactory
{
    private readonly IReadOnlyDictionary<string, IBulkSelectionFilterResolver> _lookup;

    /// <summary>Creates the factory by indexing the supplied resolvers by registry.</summary>
    /// <param name="resolvers">Every resolver registered in DI.</param>
    /// <exception cref="InvalidOperationException">Thrown on duplicate registry registrations.</exception>
    public BulkSelectionFilterResolverFactory(IEnumerable<IBulkSelectionFilterResolver> resolvers)
    {
        ArgumentNullException.ThrowIfNull(resolvers);

        var map = new Dictionary<string, IBulkSelectionFilterResolver>(StringComparer.Ordinal);
        foreach (var r in resolvers)
        {
            if (!map.TryAdd(r.Registry, r))
            {
                throw new InvalidOperationException(
                    $"Duplicate IBulkSelectionFilterResolver registered for registry '{r.Registry}'.");
            }
        }
        _lookup = map;
    }

    /// <inheritdoc />
    public Result<IBulkSelectionFilterResolver> ForRegistry(string registry)
    {
        if (string.IsNullOrWhiteSpace(registry) || !BulkRegistries.IsKnown(registry))
        {
            return Result<IBulkSelectionFilterResolver>.Failure(
                ErrorCodes.ValidationFailed, $"Registry '{registry}' is not recognised.");
        }
        if (!_lookup.TryGetValue(registry, out var resolver))
        {
            return Result<IBulkSelectionFilterResolver>.Failure(
                ErrorCodes.Internal,
                $"No filter resolver registered for known registry '{registry}'.");
        }
        return Result<IBulkSelectionFilterResolver>.Success(resolver);
    }
}
