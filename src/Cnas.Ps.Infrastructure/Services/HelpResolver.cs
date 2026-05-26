using System.Collections.Concurrent;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Help;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Reference implementation of <see cref="IHelpResolver"/>. Maintains an in-memory
/// snapshot of <c>cnas.HelpTopics</c> + <c>cnas.HelpTopicTranslations</c> that the
/// contextual-help widget consults on every render without paying for a DB round
/// trip. Refresh cadence + invalidation contract mirrors
/// <see cref="TranslationResolver"/> (R0225 / TOR UI 015).
/// </summary>
/// <remarks>
/// <para>
/// <b>Snapshot atomicity.</b> Single
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> instance replaced atomically by
/// <see cref="Interlocked.Exchange{T}(ref T, T)"/>. Per-topic DTO instances are
/// immutable records — concurrent readers can hold references safely across refreshes.
/// </para>
/// <para>
/// <b>Lifetime.</b> Singleton — the cache outlives any single scope.
/// </para>
/// </remarks>
public sealed class HelpResolver : IHelpResolver
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ISqidService _sqids;
    private readonly ILogger<HelpResolver> _logger;

    /// <summary>
    /// Current snapshot keyed by topic <see cref="Cnas.Ps.Core.Domain.HelpTopic.Code"/>.
    /// Replaced atomically by <see cref="InvalidateAsync"/> and the background
    /// refresh job; read directly by <see cref="GetByCodeAsync"/>.
    /// </summary>
    private ConcurrentDictionary<string, HelpTopicDto> _snapshot = new(StringComparer.Ordinal);

    /// <summary>Constructs the resolver with its DI dependencies.</summary>
    /// <param name="scopes">Scope factory used to materialise <see cref="IReadOnlyCnasDbContext"/> per refresh.</param>
    /// <param name="sqids">Encoder used to render the Sqid id fields on the cached DTOs.</param>
    /// <param name="logger">Structured logger for refresh diagnostics.</param>
    public HelpResolver(
        IServiceScopeFactory scopes,
        ISqidService sqids,
        ILogger<HelpResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _sqids = sqids;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<HelpTopicDto?> GetByCodeAsync(string code, string language, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        // Language is informational; the resolver returns the full translation list
        // and the caller picks the preferred language client-side.
        _ = language;

        var current = _snapshot;
        return current.TryGetValue(code, out var topic)
            ? Task.FromResult<HelpTopicDto?>(topic)
            : Task.FromResult<HelpTopicDto?>(null);
    }

    /// <inheritdoc />
    public async Task InvalidateAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IReadOnlyCnasDbContext>();

        // Pull topics + every translation row in one round trip and project into the
        // wire DTO shape. Soft-deleted rows are excluded.
        var topics = await db.HelpTopics
            .Where(t => t.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var translations = await db.HelpTopicTranslations
            .Where(t => t.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var translationsByTopic = translations
            .GroupBy(t => t.HelpTopicId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var next = new ConcurrentDictionary<string, HelpTopicDto>(StringComparer.Ordinal);
        foreach (var topic in topics)
        {
            translationsByTopic.TryGetValue(topic.Id, out var topicTranslations);
            var translationDtos = (topicTranslations ?? new())
                .Select(tr => new HelpTopicTranslationDto(
                    Id: _sqids.Encode(tr.Id),
                    Language: tr.Language,
                    Title: tr.Title,
                    BodyMarkdown: tr.BodyMarkdown,
                    IsApproved: tr.IsApproved,
                    TranslatorNote: tr.TranslatorNote))
                .ToList();

            next[topic.Code] = new HelpTopicDto(
                Id: _sqids.Encode(topic.Id),
                Code: topic.Code,
                Module: topic.Module,
                AnchorSelector: topic.AnchorSelector,
                IsActive: topic.IsActive,
                Translations: translationDtos);
        }

        Interlocked.Exchange(ref _snapshot, next);
        _logger.LogDebug("HelpResolver snapshot rebuilt with {Count} topics.", next.Count);
    }

    /// <summary>Test seam — current snapshot size.</summary>
    internal int SnapshotCount => _snapshot.Count;
}
