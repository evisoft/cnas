using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Core.Audit;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Persistence.Interceptors;

/// <summary>
/// R0191 / TOR SEC 050 / TOR ARH 028 — universal EF Core
/// <see cref="ISaveChangesInterceptor"/> that mirrors every Insert / Update /
/// Delete on a <see cref="IHistoryTracked"/> entity into a corresponding
/// <see cref="EntityHistoryRow"/>. The history projection sits alongside the
/// existing <c>AuditingInterceptor</c> — they're complementary: the auditor
/// emits an event-of-change row, the history interceptor emits a
/// state-at-instant snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two-phase capture.</b> Snapshots are computed in <c>SavingChangesAsync</c>
/// (BEFORE the row is updated, so OriginalValues / CurrentValues are still
/// addressable for the Delete / Update cases) and persisted in
/// <c>SavedChangesAsync</c> (AFTER the business write commits, so a rolled-back
/// transaction does NOT leave phantom history rows). The history row insert is
/// itself a <c>SaveChanges</c> call against the same context; a thread-local
/// re-entrance guard prevents the interceptor from recursing into its own
/// flush.
/// </para>
/// <para>
/// <b>PII discipline.</b> Column exclusion follows the same backstop list as
/// <c>AuditingInterceptor.ExcludedPropertyNames</c> + per-property
/// <c>NotAuditedAttribute</c>; the resulting JSON is additionally passed through
/// <see cref="PiiRedactor"/> as defence in depth. The payload is size-capped to
/// <see cref="EntityHistoryRow.MaxPayloadChars"/>.
/// </para>
/// <para>
/// <b>Degrades safely.</b> Without <see cref="ICnasTimeProvider"/> or
/// <see cref="ICallerContext"/> the interceptor falls back to
/// <c>DateTime.UtcNow</c> and the literal <c>"system"</c> actor — used by
/// minimal-DI test scopes that exercise persistence wiring without the full
/// application layer. Production hosts always have both wired through
/// <see cref="Cnas.Ps.Infrastructure.InfrastructureServiceCollectionExtensions.AddCnasInfrastructure"/>.
/// </para>
/// <para>
/// <b>Lifetime.</b> Scoped — same as <see cref="AuditingInterceptor"/>. Per-
/// SaveChanges state lives on the instance until the matching
/// <c>SavedChangesAsync</c> / <c>SaveChangesFailedAsync</c> fires.
/// </para>
/// </remarks>
public sealed class HistoryTrackingInterceptor : SaveChangesInterceptor
{
    /// <summary>JSON-writer cache shared across snapshots.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>
    /// Re-entrance guard. When the interceptor's own <c>SaveChangesAsync</c>
    /// fires to persist the history rows, the second pass MUST be a no-op —
    /// otherwise the interceptor would attempt to capture itself ad infinitum.
    /// </summary>
    private static readonly AsyncLocal<bool> InsideHistoryFlush = new();

    /// <summary>
    /// Per-CLR-type cache of <see cref="NotAuditedAttribute"/>-marked property
    /// names. Hot-path optimisation — reflection-based discovery would
    /// otherwise run on every entity per save.
    /// </summary>
    private static readonly Dictionary<Type, HashSet<string>> NotAuditedCache = new();
    private static readonly object NotAuditedCacheLock = new();

    private readonly ILogger<HistoryTrackingInterceptor> _logger;
    private readonly ICnasTimeProvider? _clock;
    private readonly ICallerContext? _caller;

    /// <summary>
    /// Snapshots queued during <c>SavingChangesAsync</c> and drained in
    /// <c>SavedChangesAsync</c>. Held on the instance because the interceptor
    /// is Scoped (single request = single instance). Implemented as a
    /// <see cref="ConcurrentQueue{T}"/> so an EF-abuse callsite (parallel
    /// saves over the same context) cannot corrupt the queue's backing
    /// store with race-window adds/clears — a defensive guard inside
    /// <c>SavingChangesAsync</c> throws when concurrent fires are observed,
    /// but the queue itself stays consistent even if that guard is bypassed.
    /// </summary>
    private readonly ConcurrentQueue<PendingHistoryCapture> _pending = new();

    /// <summary>
    /// Set to <c>1</c> while a save is in flight. Trips an
    /// <see cref="InvalidOperationException"/> on concurrent SavingChanges
    /// fires — EF Core abuse otherwise loses the saves-paired-with-flushes
    /// invariant.
    /// </summary>
    private int _saveInFlight;

    /// <summary>Constructs the interceptor with its scoped collaborators.</summary>
    /// <param name="logger">Structured logger.</param>
    /// <param name="clock">UTC clock abstraction; optional for minimal-DI test scopes.</param>
    /// <param name="caller">Caller-context for actor attribution; optional for minimal-DI test scopes.</param>
    public HistoryTrackingInterceptor(
        ILogger<HistoryTrackingInterceptor> logger,
        ICnasTimeProvider? clock = null,
        ICallerContext? caller = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _clock = clock;
        _caller = caller;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        // The history flush we trigger ourselves must skip capture — the new
        // EntityHistoryRow inserts would otherwise attempt to capture themselves
        // (and they're not IHistoryTracked anyway, but the guard documents the
        // contract clearly). The InsideHistoryFlush re-entrance ALSO sidesteps
        // the concurrent-save guard below, because our own flush is a nested
        // SaveChanges on the same context.
        if (InsideHistoryFlush.Value)
        {
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
        if (Interlocked.CompareExchange(ref _saveInFlight, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "HistoryTrackingInterceptor observed a concurrent SaveChanges on the same DbContext scope — EF Core does not support parallel saves. History captures from the prior save have not been drained.");
        }
        if (eventData.Context is not null)
        {
            CapturePending(eventData.Context);
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        try
        {
            return await SavedChangesAsyncCore(eventData, result, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Release the save-in-flight slot. Skipped for re-entrant flushes
            // because they never claimed the slot in the first place.
            if (!InsideHistoryFlush.Value)
            {
                Interlocked.Exchange(ref _saveInFlight, 0);
            }
        }
    }

    /// <summary>
    /// Drains the pending capture queue, persists the corresponding
    /// <see cref="EntityHistoryRow"/> entries, and chains to the base
    /// <c>SavedChangesAsync</c>. Extracted so the surrounding
    /// <c>SavedChangesAsync</c> can release the in-flight slot in a single
    /// finally clause regardless of which branch returns.
    /// </summary>
    private async ValueTask<int> SavedChangesAsyncCore(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken)
    {
        if (_pending.IsEmpty || InsideHistoryFlush.Value)
        {
            return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
        }

        // Drain by dequeuing so a re-entrant fire (rare but possible through
        // batched event handlers) doesn't double-emit.
        var batch = new List<PendingHistoryCapture>(_pending.Count);
        while (_pending.TryDequeue(out var item))
        {
            batch.Add(item);
        }

        if (eventData.Context is null)
        {
            return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
        }

        var context = eventData.Context;
        var historySet = context.Set<EntityHistoryRow>();
        // ICnasTimeProvider is the canonical clock per CLAUDE.md / TOR ARH 022;
        // when DI hasn't wired one (minimal test scopes), defer to a fresh
        // SystemTimeProvider so the interceptor still produces a deterministic
        // UTC instant without reaching for DateTime.UtcNow directly here.
        var clock = _clock ?? new SystemTimeProvider();
        var now = clock.UtcNow;
        var actor = _caller?.UserSqid ?? "system";

        foreach (var capture in batch)
        {
            // Re-read the id from the change-tracker entry — for Added
            // entries EF populates the DB-generated key in CurrentValue
            // only after SaveChanges completes, so we must defer this read.
            var entityId = ExtractEntityId(capture.Entry);
            if (!entityId.HasValue)
            {
                continue;
            }
            historySet.Add(new EntityHistoryRow
            {
                EntityType = capture.EntityType,
                EntityId = entityId.Value,
                ChangedAtUtc = now,
                Operation = capture.Operation,
                PayloadJson = capture.PayloadJson,
                ActorSqid = actor,
                CreatedAtUtc = now,
                CreatedBy = actor,
                IsActive = true,
            });
        }

        InsideHistoryFlush.Value = true;
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // History writes MUST NOT crash the surrounding request — the
            // business write already committed. Log loudly so operators
            // notice the missing rows.
            _logger.LogError(ex,
                "HistoryTrackingInterceptor failed to persist {Count} history rows.", batch.Count);
        }
        finally
        {
            InsideHistoryFlush.Value = false;
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        DrainPending();
        if (!InsideHistoryFlush.Value)
        {
            Interlocked.Exchange(ref _saveInFlight, 0);
        }
        base.SaveChangesFailed(eventData);
    }

    /// <inheritdoc />
    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        DrainPending();
        if (!InsideHistoryFlush.Value)
        {
            Interlocked.Exchange(ref _saveInFlight, 0);
        }
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    /// <summary>
    /// Discards any pending captures. Used by the SaveChangesFailed callbacks
    /// so a rolled-back transaction does not leak phantom history rows.
    /// </summary>
    private void DrainPending()
    {
        while (_pending.TryDequeue(out _))
        {
        }
    }

    /// <summary>
    /// Walks the change tracker, picks out <see cref="IHistoryTracked"/>
    /// entries in Added / Modified / Deleted state, and builds the pending
    /// capture list. The payload JSON is computed NOW (because Modified /
    /// Deleted lose their OriginalValues once the DB commit completes) but
    /// the entry reference is also retained so the surrogate-id can be re-
    /// read post-save for Added rows whose database-generated id is not yet
    /// populated at this phase.
    /// </summary>
    /// <param name="context">The DbContext being saved.</param>
    private void CapturePending(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is not IHistoryTracked)
            {
                continue;
            }
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var clrType = entry.Entity.GetType();
            var operation = entry.State switch
            {
                EntityState.Added => "I",
                EntityState.Modified => "U",
                EntityState.Deleted => "D",
                _ => "?",
            };

            var notAudited = GetNotAuditedProperties(clrType);
            var payloadJson = BuildPayload(entry, notAudited);

            _pending.Enqueue(new PendingHistoryCapture(
                Entry: entry,
                EntityType: clrType.Name,
                Operation: operation,
                PayloadJson: payloadJson));
        }
    }

    /// <summary>
    /// Builds the JSON snapshot for one change-tracker entry, applying the
    /// shared column-exclusion list AND running the resulting JSON through
    /// <see cref="PiiRedactor"/>.
    /// </summary>
    /// <param name="entry">EF change-tracker entry.</param>
    /// <param name="notAudited">Per-type <see cref="NotAuditedAttribute"/> property names.</param>
    /// <returns>Redacted, size-capped JSON payload.</returns>
    internal static string BuildPayload(EntityEntry entry, HashSet<string> notAudited)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(notAudited);

        var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in entry.Properties)
        {
            var name = prop.Metadata.Name;
            if (AuditingInterceptor.ExcludedPropertyNames.Contains(name)
                || notAudited.Contains(name))
            {
                continue;
            }
            // Delete captures the pre-image (OriginalValue still holds the
            // about-to-be-removed row); Add / Modify capture the post-image.
            var value = entry.State == EntityState.Deleted
                ? prop.OriginalValue
                : prop.CurrentValue;
            snapshot[name] = NormaliseForJson(value);
        }

        var json = JsonSerializer.Serialize(snapshot, CachedJsonOptions);
        // Defense in depth — redactor catches PII-shaped values that slipped
        // past the column-exclusion list (e.g. a developer-defined column
        // outside the backstop).
        var redacted = PiiRedactor.Redact(json);
        return redacted.Length <= EntityHistoryRow.MaxPayloadChars
            ? redacted
            : redacted[..EntityHistoryRow.MaxPayloadChars];
    }

    /// <summary>
    /// Returns the cached set of <see cref="NotAuditedAttribute"/>-marked
    /// property names for a CLR type.
    /// </summary>
    /// <param name="clrType">Entity CLR type.</param>
    /// <returns>Property-name set (case-insensitive).</returns>
    internal static HashSet<string> GetNotAuditedProperties(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        lock (NotAuditedCacheLock)
        {
            if (NotAuditedCache.TryGetValue(clrType, out var cached))
            {
                return cached;
            }
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in clrType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (prop.GetCustomAttribute<NotAuditedAttribute>(inherit: true) is not null)
                {
                    set.Add(prop.Name);
                }
            }
            NotAuditedCache[clrType] = set;
            return set;
        }
    }

    /// <summary>
    /// Normalises a property value for JSON serialisation. Byte arrays are
    /// length-described so the payload stays compact; other types pass through.
    /// </summary>
    /// <param name="value">EF Core property value.</param>
    /// <returns>JSON-friendly representation.</returns>
    internal static object? NormaliseForJson(object? value) => value switch
    {
        null => null,
        byte[] bytes => $"bytes[{bytes.Length}]",
        _ => value,
    };

    /// <summary>
    /// Extracts the long-shaped surrogate id from a change-tracker entry.
    /// Returns <c>null</c> when the entity uses a non-long key shape.
    /// </summary>
    /// <param name="entry">Change-tracker entry.</param>
    /// <returns>Surrogate id; <c>null</c> when not addressable.</returns>
    internal static long? ExtractEntityId(EntityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null || key.Properties.Count != 1)
        {
            return null;
        }
        var prop = key.Properties[0];
        // For Added entries the DB-generated id may still be 0 until SaveChanges
        // assigns it; SavedChangesAsync re-runs CurrentValue against the same
        // entry whose Id is now populated, but our pending list was filled
        // during SavingChangesAsync. Fall back to the post-save CurrentValue
        // by reading through the entry handle (EF rewires CurrentValue after
        // identity insertion completes).
        var raw = entry.Property(prop.Name).CurrentValue;
        return raw switch
        {
            long l => l,
            int i => i,
            short s => s,
            _ => null,
        };
    }

    /// <summary>
    /// Compact internal record describing one captured change. Resolved into
    /// an <see cref="EntityHistoryRow"/> at flush time. Retaining the live
    /// <see cref="EntityEntry"/> reference lets us defer the surrogate-id
    /// read to the post-save phase (DB-generated keys aren't populated at
    /// capture time for Added entries).
    /// </summary>
    /// <param name="Entry">Change-tracker entry — id re-read post-save.</param>
    /// <param name="EntityType">CLR type name (e.g. <c>UserProfile</c>).</param>
    /// <param name="Operation">Single-character operation kind (<c>I/U/D</c>).</param>
    /// <param name="PayloadJson">Redacted, size-capped JSON snapshot.</param>
    private sealed record PendingHistoryCapture(
        EntityEntry Entry,
        string EntityType,
        string Operation,
        string PayloadJson);
}
