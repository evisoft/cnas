using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Identity;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Identity;

/// <summary>
/// R0211 / TOR UI 003 — concrete implementation of
/// <see cref="IPreferredLanguageResolver"/>. Reads the user's profile via the
/// read-replica context (<see cref="IReadOnlyCnasDbContext"/>), normalises the
/// stored language string to lowercase, falls back to <c>"ro"</c> on every
/// failure path, and emits a <c>cnas.profile.preferred_language.resolved</c>
/// counter tag for ops dashboards.
/// </summary>
public sealed class PreferredLanguageResolver : IPreferredLanguageResolver
{
    /// <summary>System default language returned when no preference can be resolved.</summary>
    public const string DefaultLanguage = "ro";

    /// <summary>Allow-list of language codes the resolver will surface; everything else falls back.</summary>
    private static readonly HashSet<string> AllowedLanguages =
        new(StringComparer.OrdinalIgnoreCase) { "ro", "en", "ru" };

    private readonly IReadOnlyCnasDbContext _read;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the resolver with its scoped collaborators.</summary>
    /// <param name="read">Read-replica context for the indexed profile lookup.</param>
    /// <param name="sqids">Sqid encoder/decoder for the inbound user sqid.</param>
    public PreferredLanguageResolver(IReadOnlyCnasDbContext read, ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(sqids);
        _read = read;
        _sqids = sqids;
    }

    /// <inheritdoc />
    public async Task<Result<string>> ResolveAsync(string? userSqid, CancellationToken cancellationToken = default)
    {
        // Defensive: empty / undecodable sqid -> default. The resolver MUST NEVER fail
        // the request pipeline; the localisation middleware always needs a culture string.
        if (string.IsNullOrWhiteSpace(userSqid))
        {
            return Emit(DefaultLanguage);
        }

        var decoded = _sqids.TryDecode(userSqid);
        if (decoded.IsFailure)
        {
            return Emit(DefaultLanguage);
        }

        var userId = decoded.Value;
        var stored = await _read.UserProfiles
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => u.PreferredLanguage)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Empty / null preference -> default. Anything outside the allow-list also
        // falls back so legacy rows with stale values don't leak through.
        var normalised = stored?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalised) || !AllowedLanguages.Contains(normalised))
        {
            return Emit(DefaultLanguage);
        }

        return Emit(normalised);
    }

    /// <summary>Emits the metric and wraps the value in a successful Result.</summary>
    /// <param name="language">Resolved language code (lowercase).</param>
    /// <returns>A successful result carrying <paramref name="language"/>.</returns>
    private static Result<string> Emit(string language)
    {
        CnasMeter.PreferredLanguageResolved.Add(1, new KeyValuePair<string, object?>("language", language));
        return Result<string>.Success(language);
    }
}
