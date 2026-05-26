namespace Cnas.Ps.Contracts;

/// <summary>
/// R0502 / R0504 / R0505 / TOR CF 01.05 / CF 01.06 / CF 01.08 — input DTO for the
/// public services-catalog endpoint (anonymous browse + export). Carried over the
/// query string of <c>GET /api/public-catalog</c> (and the export variants).
/// </summary>
/// <remarks>
/// <para>
/// <b>Public surface.</b> The endpoint is anonymous-accessible, so no
/// authentication-scoped fields appear here. Pagination uses numeric
/// <see cref="Skip"/> / <see cref="Take"/> rather than 1-based page numbers because
/// scraper-style consumers prefer the cursor-ish skip / take primitive and the
/// extra clarity is worth the divergence from the internal-API pagination shape.
/// </para>
/// <para>
/// <b>Sqid invariant.</b> No external identifiers cross this DTO inbound — the
/// catalogue is filtered by code-adjacent metadata (Q, Category) rather than by
/// passport id.
/// </para>
/// </remarks>
/// <param name="Q">
/// Optional free-text query — substring match (diacritic-insensitive) against the
/// passport <c>NameRo</c> + <c>DescriptionRo</c> fields (or the
/// <see cref="Language"/>-specific name when configured). Null / empty disables
/// the filter and triggers the budget guard's "free-text required" hint when the
/// registry exceeds its budget.
/// </param>
/// <param name="Category">
/// Optional category code (e.g. <c>"PENSIONS"</c>, <c>"FAMILY"</c>). Equality
/// match against <c>ServicePassport.Category</c>. Stable upper-snake-case
/// identifier; case-sensitive.
/// </param>
/// <param name="Sort">
/// Sort key — one of <c>"Relevance"</c>, <c>"Alphabetical"</c>, <c>"Created"</c>,
/// <c>"Updated"</c>. Parsed case-insensitively to the
/// <c>PublicCatalogSortOptions</c> enum in the validator. Defaults to
/// <c>"Relevance"</c>.
/// </param>
/// <param name="Skip">
/// Number of rows to skip (0-based offset). Clamped to <c>&gt;= 0</c> in the
/// service layer.
/// </param>
/// <param name="Take">
/// Number of rows to take. Service clamps to <c>[1, 200]</c>; the validator
/// rejects values strictly above 200 so the controller fails fast.
/// </param>
/// <param name="Language">
/// ISO-639-1 language code (<c>"ro"</c>, <c>"en"</c>, <c>"ru"</c>) controlling
/// which name/description column the output projects. Falls back to
/// <c>"ro"</c> when the requested locale's column is null. Defaults to
/// <c>"ro"</c> when omitted.
/// </param>
public sealed record PublicCatalogListQueryDto(
    string? Q = null,
    string? Category = null,
    string Sort = "Relevance",
    int Skip = 0,
    int Take = 50,
    string? Language = "ro");

/// <summary>
/// R0502 / R0505 — projection row for the public services-catalog list / export
/// endpoints. Carries the locale-resolved name + description so the caller can
/// render the catalogue card without a second round trip.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sqid invariant (CLAUDE.md RULE 3).</b> <see cref="Id"/> is the Sqid-encoded
/// passport row id; the stable business <see cref="Code"/> is intentionally a
/// separate field because external integrators bind to it.
/// </para>
/// <para>
/// <b>Stability.</b> The shape is part of the public API contract — additive
/// changes only. The export-CSV layout mirrors these columns 1:1, so any rename
/// here is a CSV header change for downstream consumers.
/// </para>
/// </remarks>
/// <param name="Id">Sqid-encoded passport row id.</param>
/// <param name="Code">Stable business code (e.g. <c>SP-001-BIRTH</c>); not a Sqid.</param>
/// <param name="Name">Locale-resolved display name.</param>
/// <param name="Description">Locale-resolved description; nullable when none is configured.</param>
/// <param name="Category">Optional category code; null on legacy uncategorised rows.</param>
/// <param name="Version">Current revision number (R0129 / CF 15.04).</param>
/// <param name="UpdatedAtUtc">Most-recent update timestamp; falls back to <c>CreatedAtUtc</c> when the row was never updated.</param>
public sealed record PublicCatalogListItemDto(
    string Id,
    string Code,
    string Name,
    string? Description,
    string? Category,
    int Version,
    DateTime UpdatedAtUtc);
