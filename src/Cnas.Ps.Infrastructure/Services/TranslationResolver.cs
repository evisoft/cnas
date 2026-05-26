using System.Collections.Concurrent;
using System.Collections.Generic;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Localization;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Reference implementation of <see cref="ITranslationResolver"/>. Maintains an
/// in-memory snapshot of <c>cnas.TranslationKeys</c> + <c>cnas.TranslationValues</c>
/// that callers (Blazor renderer, email templating pipeline) consult on every render
/// without paying for a DB round-trip. The snapshot is rebuilt by
/// <c>TranslationCacheRefreshJob</c> on a 60 s cadence by default; the CRUD
/// value-side service additionally triggers a synchronous refresh via
/// <see cref="InvalidateAsync"/> after every mutation (mirrors the R0182 audit policy
/// resolver pattern).
/// </summary>
/// <remarks>
/// <para>
/// <b>Snapshot atomicity.</b> The snapshot is a single
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> instance; refresh replaces the
/// instance via <see cref="Interlocked.Exchange{T}(ref T, T)"/> so concurrent
/// <see cref="Resolve"/> readers always see a consistent map. The dictionary lookup
/// itself is lock-free.
/// </para>
/// <para>
/// <b>Lifetime.</b> Registered as a singleton because the cache state must outlive
/// any single scope. The refresh job resolves the singleton via the DI scope factory.
/// </para>
/// <para>
/// <b>Cache-miss counter.</b> When the exact (code, language) lookup misses, the
/// resolver bumps <see cref="CnasMeter.TranslationMiss"/> tagged with the requested
/// language + code so operators can chart which strings most often fall through to
/// the RO fallback.
/// </para>
/// </remarks>
public sealed class TranslationResolver : ITranslationResolver
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TranslationResolver> _logger;

    /// <summary>
    /// Current snapshot. Keyed by (Code, Language). Replaced atomically by
    /// <see cref="InvalidateAsync"/> and the background refresh job; read directly by
    /// <see cref="Resolve"/>. Starts as an empty map so the resolver is safe to query
    /// before the first refresh completes — the fallback path handles the no-match
    /// case.
    /// </summary>
    private ConcurrentDictionary<TranslationKey, string> _snapshot = new();

    /// <summary>Constructs the resolver with its DI scope factory + logger.</summary>
    /// <param name="scopes">Scope factory used to materialise <see cref="IReadOnlyCnasDbContext"/> per refresh.</param>
    /// <param name="logger">Structured logger for refresh diagnostics.</param>
    public TranslationResolver(IServiceScopeFactory scopes, ILogger<TranslationResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Resolve(string code, string language, string? fallback = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        // Stable reference to the current snapshot; a concurrent refresh swap is
        // invisible to this in-flight call.
        var current = _snapshot;

        // 1) Exact (code, language) hit.
        if (current.TryGetValue(new TranslationKey(code, language), out var hit))
        {
            return hit;
        }

        // 2) Fall back to RO when the requested language is not already RO. Bump the
        //    miss counter tagged with the requested language so operators chart which
        //    locales lag behind.
        if (!string.Equals(language, TranslationLanguages.Romanian, StringComparison.Ordinal))
        {
            CnasMeter.TranslationMiss.Add(1,
                new KeyValuePair<string, object?>("language", language),
                new KeyValuePair<string, object?>("code", code));
            if (current.TryGetValue(new TranslationKey(code, TranslationLanguages.Romanian), out var roHit))
            {
                return roHit;
            }
        }
        else
        {
            // Even the RO branch counts — operators want visibility on every miss.
            CnasMeter.TranslationMiss.Add(1,
                new KeyValuePair<string, object?>("language", language),
                new KeyValuePair<string, object?>("code", code));
        }

        // 3) Final fallback — caller's fallback wins; otherwise return the code so
        //    the missing string is visible inline to QA.
        return fallback ?? code;
    }

    /// <inheritdoc />
    public async Task InvalidateAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IReadOnlyCnasDbContext>();

        // Join keys + values so we can build a (code, language) → text map in one
        // round trip. Soft-deleted keys (IsActive=false) drop their values.
        var rows = await (from v in db.TranslationValues
                          join k in db.TranslationKeys on v.TranslationKeyId equals k.Id
                          where k.IsActive && v.IsActive
                          select new { k.Code, v.Language, v.Text })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var next = new ConcurrentDictionary<TranslationKey, string>();
        foreach (var r in rows)
        {
            next[new TranslationKey(r.Code, r.Language)] = r.Text;
        }

        Interlocked.Exchange(ref _snapshot, next);
        _logger.LogDebug(
            "TranslationResolver snapshot rebuilt with {Count} (code, language) entries.",
            next.Count);
    }

    /// <summary>
    /// Test seam — returns the current snapshot size. Used by integration tests to
    /// assert that <see cref="InvalidateAsync"/> picked up newly inserted rows.
    /// </summary>
    internal int SnapshotCount => _snapshot.Count;

    /// <summary>
    /// Composite snapshot key. Ordinal string comparison is the default for record
    /// struct equality on string fields — case-sensitive on both segments which
    /// matches the registry's canonical-lowercase convention.
    /// </summary>
    /// <param name="Code">Stable kebab-case translation key.</param>
    /// <param name="Language">ISO-639-1 language code.</param>
    private readonly record struct TranslationKey(string Code, string Language);
}
