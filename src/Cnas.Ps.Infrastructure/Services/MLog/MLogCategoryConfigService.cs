using System.Collections.Concurrent;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.MLog;

/// <summary>
/// R0116 + R0195 / TOR SEC 054-055 — concrete implementation of
/// <see cref="IMLogCategoryConfigService"/>. Persists the operator-tuned
/// dual-write filter rows and signals the in-memory
/// <see cref="MLogCategoryFilter"/> to refresh its snapshot on every mutation.
/// </summary>
public sealed class MLogCategoryConfigService : IMLogCategoryConfigService
{
    /// <summary>Audit code emitted on every state-changing upsert.</summary>
    public const string AuditUpserted = "MLOG.CATEGORY.UPSERTED";

    /// <summary>Audit code emitted on deactivate.</summary>
    public const string AuditDeactivated = "MLOG.CATEGORY.DEACTIVATED";

    private readonly ICnasDbContext _db;
    private readonly IReadOnlyCnasDbContext _readOnlyDb;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IMLogCategoryFilterCache? _cache;

    /// <summary>Constructs the service.</summary>
    /// <param name="db">Per-request write context.</param>
    /// <param name="readOnlyDb">Per-request read-only context.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="clock">UTC clock.</param>
    /// <param name="caller">Authenticated caller information.</param>
    /// <param name="audit">Audit journal façade.</param>
    /// <param name="cache">Optional in-memory filter snapshot; invalidated on mutation.</param>
    public MLogCategoryConfigService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext readOnlyDb,
        ISqidService sqids,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IAuditService audit,
        IMLogCategoryFilterCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(readOnlyDb);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);

        _db = db;
        _readOnlyDb = readOnlyDb;
        _sqids = sqids;
        _clock = clock;
        _caller = caller;
        _audit = audit;
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<MLogCategoryConfigDto>>> ListAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var query = _readOnlyDb.MLogCategoryConfigs.AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }
        var rows = await query
            .OrderBy(c => c.CategoryCode)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<MLogCategoryConfigDto> dtos = rows.Select(Project).ToList();
        return Result<IReadOnlyList<MLogCategoryConfigDto>>.Success(dtos);
    }

    /// <inheritdoc />
    public async Task<Result<MLogCategoryConfigDto>> UpsertAsync(
        MLogCategoryConfigInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var shape = ValidateInput(input);
        if (shape.IsFailure)
        {
            return Result<MLogCategoryConfigDto>.Failure(shape.ErrorCode!, shape.ErrorMessage!);
        }

        var existing = await _db.MLogCategoryConfigs
            .SingleOrDefaultAsync(c => c.CategoryCode == input.CategoryCode, cancellationToken)
            .ConfigureAwait(false);

        var now = _clock.UtcNow;
        var minSeverity = (MLogSeverityFloor)(int)input.MinSeverity;
        bool isInsert = existing is null;
        MLogCategoryConfig row;
        if (isInsert)
        {
            row = new MLogCategoryConfig
            {
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                CategoryCode = input.CategoryCode,
                DisplayName = input.DisplayName,
                IsEnabled = input.IsEnabled,
                MinSeverity = minSeverity,
                UpdatedByUserId = _caller.UserId,
                IsActive = true,
            };
            _db.MLogCategoryConfigs.Add(row);
        }
        else
        {
            row = existing!;
            row.DisplayName = input.DisplayName;
            row.IsEnabled = input.IsEnabled;
            row.MinSeverity = minSeverity;
            row.IsActive = true;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = _caller.UserSqid;
            row.UpdatedByUserId = _caller.UserId;
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache?.Invalidate();

        var detailsJson = JsonSerializer.Serialize(new
        {
            configId = row.Id,
            categoryCode = row.CategoryCode,
            isEnabled = row.IsEnabled,
            minSeverity = row.MinSeverity.ToString(),
            inserted = isInsert,
        });
        await _audit.RecordAsync(
            AuditUpserted,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(MLogCategoryConfig),
            row.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return Result<MLogCategoryConfigDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result> DeactivateAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _db.MLogCategoryConfigs
            .SingleOrDefaultAsync(c => c.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, $"MLog category id={decoded.Value} not found.");
        }
        if (!row.IsActive)
        {
            return Result.Success();
        }
        var now = _clock.UtcNow;
        row.IsActive = false;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        row.UpdatedByUserId = _caller.UserId;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache?.Invalidate();

        var detailsJson = JsonSerializer.Serialize(new
        {
            configId = row.Id,
            categoryCode = row.CategoryCode,
        });
        await _audit.RecordAsync(
            AuditDeactivated,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(MLogCategoryConfig),
            row.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>Projects a domain row into the wire DTO.</summary>
    private MLogCategoryConfigDto Project(MLogCategoryConfig row) => new(
        Sqid: _sqids.Encode(row.Id),
        CategoryCode: row.CategoryCode,
        DisplayName: row.DisplayName,
        IsEnabled: row.IsEnabled,
        MinSeverity: (MLogSeverityFloorDto)(int)row.MinSeverity,
        IsActive: row.IsActive,
        UpdatedAtUtc: row.UpdatedAtUtc);

    /// <summary>Defence-in-depth shape validation.</summary>
    private static Result ValidateInput(MLogCategoryConfigInputDto input)
    {
        if (string.IsNullOrWhiteSpace(input.CategoryCode)
            || input.CategoryCode.Length > MLogCategoryConfig.MaxCategoryCodeLength)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "CategoryCode is required (≤ 64 chars).");
        }
        if (string.IsNullOrWhiteSpace(input.DisplayName)
            || input.DisplayName.Length > MLogCategoryConfig.MaxDisplayNameLength)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "DisplayName is required (≤ 256 chars).");
        }
        if (!Enum.IsDefined(input.MinSeverity))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "MinSeverity is invalid.");
        }
        return Result.Success();
    }
}

/// <summary>
/// R0116 + R0195 — singleton cache for the active MLog dual-write filter
/// snapshot. Invalidated on every <see cref="MLogCategoryConfigService"/>
/// mutation; reloaded lazily on next read.
/// </summary>
public interface IMLogCategoryFilterCache
{
    /// <summary>Invalidates the in-memory snapshot.</summary>
    void Invalidate();
}

/// <summary>
/// R0116 + R0195 — singleton in-memory filter snapshot consulted by the
/// audit drainer's MLog dual-write fork. Reads the active filter rows once on
/// first use and refreshes on demand when
/// <see cref="IMLogCategoryFilterCache.Invalidate"/> is called.
/// </summary>
public sealed class MLogCategoryFilter : IMLogCategoryFilter, IMLogCategoryFilterCache
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MLogCategoryFilter> _logger;
    private readonly ConcurrentDictionary<string, FilterEntry> _snapshot = new(StringComparer.Ordinal);
    private volatile bool _loaded;
    private readonly object _gate = new();

    /// <summary>Constructs the snapshot.</summary>
    /// <param name="scopes">Scope factory used to resolve the read-only context for refresh.</param>
    /// <param name="logger">Structured logger; logs refresh failures at Warning.</param>
    public MLogCategoryFilter(
        IServiceScopeFactory scopes,
        ILogger<MLogCategoryFilter> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool ShouldMirror(string eventCode, AuditSeverity severity)
    {
        if (!_loaded)
        {
            EnsureLoaded();
        }

        // Pre-R0195 default: only Critical events mirrored. Applied when NO matching
        // row is found in the registry.
        if (_snapshot.IsEmpty)
        {
            return severity == AuditSeverity.Critical;
        }

        var match = MatchRow(eventCode);
        if (match is null)
        {
            return severity == AuditSeverity.Critical;
        }
        if (!match.IsEnabled)
        {
            return false;
        }
        return match.MinSeverity switch
        {
            MLogSeverityFloor.Critical => severity == AuditSeverity.Critical,
            MLogSeverityFloor.Notice => severity != AuditSeverity.Information,
            _ => severity == AuditSeverity.Critical,
        };
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        _loaded = false;
    }

    /// <summary>Loads the snapshot from the read-only DB on first use (or after invalidation).</summary>
    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }
            _snapshot.Clear();
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IReadOnlyCnasDbContext>();
                var rows = db.MLogCategoryConfigs
                    .Where(c => c.IsActive)
                    .ToList();
                foreach (var row in rows)
                {
                    _snapshot[row.CategoryCode] = new FilterEntry(row.IsEnabled, row.MinSeverity);
                }
                _loaded = true;
            }
#pragma warning disable CA1031 // Best-effort refresh — defaults to pre-R0195 behaviour on failure.
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "MLogCategoryFilter snapshot refresh failed; falling back to Critical-only mirror.");
                _snapshot.Clear();
                _loaded = true; // avoid hot-loop on repeated failures
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>
    /// Resolves the most-specific filter row for an event code: tries the full
    /// code first, then progressively shorter dotted-prefix segments
    /// (<c>APPLICATION.RECEIVE.SUBMITTED</c> → <c>APPLICATION.RECEIVE</c> →
    /// <c>APPLICATION</c>). Returns <c>null</c> when no match exists.
    /// </summary>
    private FilterEntry? MatchRow(string eventCode)
    {
        if (string.IsNullOrEmpty(eventCode))
        {
            return null;
        }
        var span = eventCode;
        while (!string.IsNullOrEmpty(span))
        {
            if (_snapshot.TryGetValue(span, out var entry))
            {
                return entry;
            }
            var dot = span.LastIndexOf('.');
            if (dot < 0)
            {
                return null;
            }
            span = span[..dot];
        }
        return null;
    }

    /// <summary>Internal snapshot row.</summary>
    private sealed record FilterEntry(bool IsEnabled, MLogSeverityFloor MinSeverity);
}
