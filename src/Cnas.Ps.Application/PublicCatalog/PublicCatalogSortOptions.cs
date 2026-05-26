namespace Cnas.Ps.Application.PublicCatalog;

/// <summary>
/// R0502 / TOR CF 01.05 — sort-order options exposed by the public services-catalog
/// list / export endpoints. Bound from the inbound DTO's string <c>Sort</c> field
/// via case-insensitive enum parsing in <c>PublicCatalogListQueryValidator</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stability.</b> Enum members are part of the public API contract — the UI
/// binds button labels to these names. Adding new members is additive; renaming
/// existing ones is a breaking change.
/// </para>
/// </remarks>
public enum PublicCatalogSortOptions
{
    /// <summary>
    /// Default. Full-text relevance — Postgres <c>unaccent + ILIKE</c> path scores
    /// matches; the InMemory test fallback uses a <c>StartsWith</c> = 3 /
    /// <c>Contains</c> = 1 heuristic to keep behaviour deterministic without
    /// requiring the Postgres extension. When the caller did not supply a free-text
    /// query the relevance score is moot — the service falls through to
    /// <see cref="Updated"/>.
    /// </summary>
    Relevance = 0,

    /// <summary>
    /// Alphabetical by locale-resolved passport <c>Name</c>, ascending. Stable
    /// sort by the passport's primary-key id for deterministic ordering when two
    /// rows share the same name.
    /// </summary>
    Alphabetical = 1,

    /// <summary>
    /// Most-recently created first (<c>CreatedAtUtc</c> DESC). Used by the
    /// "newest" UI tab. Ties broken by primary-key id DESC.
    /// </summary>
    Created = 2,

    /// <summary>
    /// Most-recently updated first (<c>UpdatedAtUtc</c> DESC, falling back to
    /// <c>CreatedAtUtc</c> for rows that have never been updated). Used by the
    /// "what changed lately?" UI tab.
    /// </summary>
    Updated = 3,
}
