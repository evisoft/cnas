namespace Cnas.Ps.Application.Localization;

/// <summary>
/// R0210 / TOR UI 007 / CF 17.16 — hot-path translation lookup consulted on every
/// Blazor render and every email-template generation. Returns the localised string
/// for a (code, language) pair, falling back to RO and finally to the caller-supplied
/// fallback (or the code itself) so missing strings stay visible to operators rather
/// than silently disappearing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hot-path discipline.</b> Implementations MUST be non-blocking and allocation-
/// free on the cache-hit path. The reference implementation backs this with an
/// in-memory <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>
/// snapshot rebuilt on a 60 s cadence by a hosted refresh job and on demand by the
/// CRUD value-side service.
/// </para>
/// <para>
/// <b>Fallback chain.</b>
/// <list type="number">
///   <item>Lookup the exact (code, language) — return the text on hit.</item>
///   <item>When the language is not <c>"ro"</c>, look up (code, <c>"ro"</c>) — return the RO text on hit, increment <c>cnas.translation.miss</c> tagged with the requested language.</item>
///   <item>When both miss, increment <c>cnas.translation.miss</c> and return the caller-supplied fallback (or the code itself when no fallback was supplied).</item>
/// </list>
/// </para>
/// <para>
/// <b>Why the code-as-fallback default.</b> Returning an obvious placeholder string
/// (e.g. <c>"@@MISSING@@"</c>) hides the actual key from operators and complicates
/// diagnostics. Returning the raw code makes the missing string visible inline so
/// QA can spot it immediately and add the translation without grep-hunting through
/// markup.
/// </para>
/// </remarks>
public interface ITranslationResolver
{
    /// <summary>
    /// Returns the localised text for (<paramref name="code"/>, <paramref name="language"/>),
    /// applying the documented fallback chain on miss.
    /// </summary>
    /// <param name="code">Stable kebab-case key (e.g. <c>pages.applications.list.title</c>).</param>
    /// <param name="language">ISO-639-1 language code; non-canonical values fall through to the RO branch.</param>
    /// <param name="fallback">Optional explicit fallback returned when both the requested language and RO miss.</param>
    /// <returns>The resolved text — guaranteed non-null.</returns>
    string Resolve(string code, string language, string? fallback = null);

    /// <summary>
    /// Rebuilds the in-memory snapshot from the latest persisted state. Invoked by
    /// the background refresh job on its cadence and synchronously by the value-side
    /// CRUD service after every mutation so changes are visible to the next call.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task completing when the snapshot has been swapped.</returns>
    Task InvalidateAsync(CancellationToken ct = default);
}
