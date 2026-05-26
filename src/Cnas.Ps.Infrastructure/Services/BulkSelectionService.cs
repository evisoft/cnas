using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.BulkActions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — default <see cref="IBulkSelectionService"/>
/// implementation backed by <see cref="ICnasDbContext"/>. Persists the registry + filter
/// + include/exclude triple as a durable handle; re-resolves the filter against the
/// live DB at consume time so the operation runs against the current row set (no
/// TOCTOU drift). See the interface XML doc for the access rules.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolver dispatch.</b> The service consults
/// <see cref="IBulkSelectionFilterResolverFactory"/> for a per-registry resolver. A
/// missing resolver for a registered registry is a configuration bug and surfaces as
/// <see cref="ErrorCodes.Internal"/> rather than as a silent empty result.
/// </para>
/// <para>
/// <b>Resolved-count snapshot.</b> <see cref="CreateAsync"/> calls the resolver once
/// at create time to populate <c>ResolvedCount</c> on the persisted row. That number
/// is informational — the runner re-resolves at run time. The snapshot lets a UI
/// render the right preview ("operate on N rows") without needing a second
/// round-trip; if the live count drifts before the run, the UI's preview is
/// stale but the operation still runs against the truth.
/// </para>
/// </remarks>
public sealed class BulkSelectionService : IBulkSelectionService
{
    private readonly ICnasDbContext _db;
    private readonly ICallerContext _caller;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly IBulkSelectionFilterResolverFactory _resolverFactory;
    private readonly BulkSelectionOptions _opts;

    /// <summary>Creates the bulk-selection service.</summary>
    /// <param name="db">Per-request CNAS DbContext.</param>
    /// <param name="caller">Per-request caller context (auth identity).</param>
    /// <param name="sqids">Sqid encoder used to surface external ids.</param>
    /// <param name="clock">UTC clock used to stamp <c>CreatedAtUtc</c> / <c>ExpiresAtUtc</c>.</param>
    /// <param name="resolverFactory">Per-registry filter-resolver factory.</param>
    /// <param name="options">Bound bulk-selection options (lifetime / caps).</param>
    public BulkSelectionService(
        ICnasDbContext db,
        ICallerContext caller,
        ISqidService sqids,
        ICnasTimeProvider clock,
        IBulkSelectionFilterResolverFactory resolverFactory,
        IOptions<BulkSelectionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(resolverFactory);
        ArgumentNullException.ThrowIfNull(options);

        _db = db;
        _caller = caller;
        _sqids = sqids;
        _clock = clock;
        _resolverFactory = resolverFactory;
        _opts = options.Value;
    }

    /// <inheritdoc />
    public async Task<Result<BulkSelectionOutputDto>> CreateAsync(
        string registry,
        string filterJson,
        IReadOnlyList<long>? explicitInclude,
        IReadOnlyList<long>? explicitExclude,
        CancellationToken ct = default)
    {
        if (_caller.UserId is not long ownerId)
        {
            return Result<BulkSelectionOutputDto>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }
        if (string.IsNullOrWhiteSpace(registry) || !BulkRegistries.IsKnown(registry))
        {
            return Result<BulkSelectionOutputDto>.Failure(
                ErrorCodes.ValidationFailed, "Registry is unknown.");
        }
        if (string.IsNullOrWhiteSpace(filterJson))
        {
            return Result<BulkSelectionOutputDto>.Failure(
                ErrorCodes.ValidationFailed, "FilterJson is required.");
        }
        if (System.Text.Encoding.UTF8.GetByteCount(filterJson) > _opts.MaxFilterJsonLength)
        {
            return Result<BulkSelectionOutputDto>.Failure(
                ErrorCodes.ValidationFailed,
                $"FilterJson exceeds the {_opts.MaxFilterJsonLength}-byte cap.");
        }
        var includeList = (explicitInclude ?? Array.Empty<long>()).ToList();
        var excludeList = (explicitExclude ?? Array.Empty<long>()).ToList();
        if (includeList.Count > _opts.MaxExplicitIdsPerList
            || excludeList.Count > _opts.MaxExplicitIdsPerList)
        {
            return Result<BulkSelectionOutputDto>.Failure(
                ErrorCodes.ValidationFailed,
                $"Explicit id lists exceed the {_opts.MaxExplicitIdsPerList}-entry cap.");
        }

        // Snapshot the resolved count by running the filter once at create time. The
        // runner re-resolves at run time so any drift between create and run is
        // handled there; this snapshot is purely informational.
        var resolverLookup = _resolverFactory.ForRegistry(registry);
        if (resolverLookup.IsFailure)
        {
            return Result<BulkSelectionOutputDto>.Failure(
                resolverLookup.ErrorCode!, resolverLookup.ErrorMessage!);
        }

        var resolved = await resolverLookup.Value.ResolveAsync(filterJson, ct).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return Result<BulkSelectionOutputDto>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
        }
        var resolvedCount = ApplyOverlays(resolved.Value, includeList, excludeList).Count;

        var now = _clock.UtcNow;
        var row = new BulkSelection
        {
            Registry = registry,
            OwnerUserId = ownerId,
            FilterJson = filterJson,
            ExplicitIncludeIds = includeList,
            ExplicitExcludeIds = excludeList,
            ResolvedCount = resolvedCount,
            ExpiresAtUtc = now + _opts.SelectionLifetime,
            IsConsumed = false,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.BulkSelections.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Result<BulkSelectionOutputDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<long>>> ResolveIdsAsync(
        long bulkSelectionId,
        CancellationToken ct = default)
    {
        // iter-149 — ownership check mirrors GetAsync. Without it a caller who
        // somehow learned (or guessed) another user's selection id could pull
        // back the resolved row set. The runner already enforces ownership in
        // its pre-loop block, but defence-in-depth at the service boundary
        // closes the gap for any other caller that bypasses the runner.
        if (_caller.UserId is not long callerId)
        {
            return Result<IReadOnlyList<long>>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }
        var row = await _db.BulkSelections
            .SingleOrDefaultAsync(s => s.Id == bulkSelectionId && s.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<IReadOnlyList<long>>.Failure(ErrorCodes.NotFound, "Bulk selection not found.");
        }
        if (row.OwnerUserId != callerId)
        {
            return Result<IReadOnlyList<long>>.Failure(
                ErrorCodes.Forbidden, "Bulk selection is owned by another user.");
        }
        if (row.ExpiresAtUtc <= _clock.UtcNow)
        {
            return Result<IReadOnlyList<long>>.Failure(
                ErrorCodes.BulkSelectionExpired, "Bulk selection has expired.");
        }

        var resolverLookup = _resolverFactory.ForRegistry(row.Registry);
        if (resolverLookup.IsFailure)
        {
            return Result<IReadOnlyList<long>>.Failure(
                resolverLookup.ErrorCode!, resolverLookup.ErrorMessage!);
        }

        var resolved = await resolverLookup.Value.ResolveAsync(row.FilterJson, ct).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return Result<IReadOnlyList<long>>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
        }

        var ids = ApplyOverlays(resolved.Value, row.ExplicitIncludeIds, row.ExplicitExcludeIds);
        return Result<IReadOnlyList<long>>.Success(ids);
    }

    /// <inheritdoc />
    public async Task<Result<BulkSelectionOutputDto>> GetAsync(string sqid, CancellationToken ct = default)
    {
        if (_caller.UserId is not long callerId)
        {
            return Result<BulkSelectionOutputDto>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<BulkSelectionOutputDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _db.BulkSelections
            .SingleOrDefaultAsync(s => s.Id == decoded.Value && s.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<BulkSelectionOutputDto>.Failure(
                ErrorCodes.NotFound, "Bulk selection not found.");
        }
        if (row.OwnerUserId != callerId)
        {
            return Result<BulkSelectionOutputDto>.Failure(
                ErrorCodes.Forbidden, "Bulk selection is owned by another user.");
        }
        return Result<BulkSelectionOutputDto>.Success(Project(row));
    }

    /// <summary>
    /// Applies the include / exclude overlays to the resolver result. The output is
    /// stable-ordered (ascending by id) so downstream callers see a deterministic
    /// row order.
    /// </summary>
    /// <param name="filterIds">Raw filter result from the resolver.</param>
    /// <param name="include">Ids to union with the filter result.</param>
    /// <param name="exclude">Ids to subtract from the result. Wins on a conflict with include.</param>
    /// <returns>Ordered, de-duplicated id list.</returns>
    private static IReadOnlyList<long> ApplyOverlays(
        IReadOnlyList<long> filterIds,
        IReadOnlyList<long> include,
        IReadOnlyList<long> exclude)
    {
        var set = new HashSet<long>(filterIds);
        foreach (var id in include)
        {
            set.Add(id);
        }
        foreach (var id in exclude)
        {
            set.Remove(id);
        }
        var list = set.ToList();
        list.Sort();
        return list;
    }

    /// <summary>
    /// Projects an entity row to its public DTO form with Sqid-encoded id.
    /// </summary>
    /// <param name="row">Loaded entity.</param>
    /// <returns>The DTO the API surfaces.</returns>
    private BulkSelectionOutputDto Project(BulkSelection row) => new(
        _sqids.Encode(row.Id),
        row.Registry,
        row.ResolvedCount,
        row.ExpiresAtUtc,
        row.IsConsumed);
}
