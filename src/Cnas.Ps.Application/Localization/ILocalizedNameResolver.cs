namespace Cnas.Ps.Application.Localization;

/// <summary>
/// R0027 / TOR ARH 022 — culture-aware display-name resolver consulted whenever a
/// caller needs the localized label of a user-facing entity carrying the optional
/// <c>NameRo</c> / <c>NameRu</c> / <c>NameEn</c> trio (see e.g.
/// <c>Cnas.Ps.Core.Domain.CnasBranch</c>, <c>SupportTicketCategory</c>,
/// <c>AuditCategory</c>, <c>ServicePassport</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a service, not a method on the entity.</b> Core / Domain may not depend on
/// Application (CLAUDE.md §1.1), and the resolution policy (fallback chain) is a
/// presentation concern. Keeping the policy in this Application-layer service lets
/// us tune it (e.g. add Ukrainian) without churning the Domain.
/// </para>
/// <para>
/// <b>Fallback chain.</b> The resolver applies the following lookups, returning the
/// FIRST non-null/non-empty match:
/// <list type="number">
///   <item>The per-culture column matching the requested <c>culture</c> argument (e.g. <c>NameRu</c> for <c>"ru"</c>).</item>
///   <item><c>NameRo</c> when the requested culture is not Romanian.</item>
///   <item><c>NameEn</c>.</item>
///   <item>The supplied <c>baseName</c> (typically the entity's original
///         <c>Name</c> / <c>DisplayName</c> column).</item>
///   <item>An empty string — only when every input is null/whitespace.</item>
/// </list>
/// The chain MUST be deterministic and allocation-free on the hot path.
/// </para>
/// <para>
/// <b>Culture matching.</b> The <c>culture</c> argument is case-insensitive and
/// only the two-letter prefix is consulted, so <c>"ro-MD"</c>, <c>"RO"</c>, and
/// <c>"ro"</c> all resolve to the Romanian column. An unknown or null culture
/// skips step 1 directly.
/// </para>
/// </remarks>
public interface ILocalizedNameResolver
{
    /// <summary>
    /// Resolves the best display-name match for the supplied per-locale trio.
    /// </summary>
    /// <param name="baseName">
    /// The entity's original required <c>Name</c> / <c>DisplayName</c> column. Used
    /// as the final fallback so legacy rows that have not yet been localized still
    /// render a label.
    /// </param>
    /// <param name="nameRo">Romanian per-locale name; may be null/whitespace.</param>
    /// <param name="nameRu">Russian per-locale name; may be null/whitespace.</param>
    /// <param name="nameEn">English per-locale name; may be null/whitespace.</param>
    /// <param name="culture">
    /// Requested culture code (e.g. <c>"ro"</c>, <c>"ro-MD"</c>, <c>"ru"</c>,
    /// <c>"en-GB"</c>). Null / empty / unknown skips step 1 of the fallback chain.
    /// </param>
    /// <returns>The resolved label — guaranteed non-null.</returns>
    string Resolve(string? baseName, string? nameRo, string? nameRu, string? nameEn, string? culture);
}
