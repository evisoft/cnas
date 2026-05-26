using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Help;

/// <summary>
/// R0225 / TOR UI 015 — public-facing resolver consulted by the contextual-help
/// widget on every render. Returns the topic plus every available per-language
/// translation so the caller can pick its preferred language client-side.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cached.</b> Implementations back the lookup with a 60 s in-memory snapshot
/// rebuilt by a hosted refresh job and on demand after CRUD mutations.
/// </para>
/// <para>
/// <b>Language semantics.</b> The <c>language</c> parameter is currently
/// informational — the resolver returns the full translation list. Callers pick the
/// preferred language client-side and fall back to RO when their preference is
/// missing. A future iteration may filter server-side when bandwidth on slow
/// connections becomes a concern.
/// </para>
/// <para>
/// <b>No-match contract.</b> Returns <c>null</c> when no topic matches the code, OR
/// when the topic exists but has zero translations. The UI suppresses the tooltip
/// in that case instead of rendering an empty bubble.
/// </para>
/// </remarks>
public interface IHelpResolver
{
    /// <summary>
    /// Returns the topic identified by <paramref name="code"/> with every persisted
    /// translation, or <c>null</c> when the topic is absent / inactive / has no
    /// translations.
    /// </summary>
    /// <param name="code">Stable kebab-case topic code.</param>
    /// <param name="language">ISO-639-1 language preference; informational.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The topic DTO on success, or <c>null</c> on miss.</returns>
    Task<HelpTopicDto?> GetByCodeAsync(string code, string language, CancellationToken ct = default);

    /// <summary>
    /// Rebuilds the in-memory snapshot from the latest persisted state. Invoked by
    /// the background refresh job on its cadence and synchronously by the CRUD
    /// services after every mutation.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task completing when the snapshot has been swapped.</returns>
    Task InvalidateAsync(CancellationToken ct = default);
}
