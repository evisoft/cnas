namespace Cnas.Ps.Contracts;

/// <summary>
/// R2190-R2200 / TOR §15.6 FLEX 006 — single dynamic attribute row as it
/// leaves the system. Backs the "list attributes for an entity" surface.
/// </summary>
/// <param name="Id">Sqid-encoded id of the underlying EAV row.</param>
/// <param name="EntityType">Logical entity kind (e.g. <c>Application</c>).</param>
/// <param name="EntitySqid">Sqid-encoded id of the host entity row.</param>
/// <param name="AttributeCode">Stable attribute code.</param>
/// <param name="Value">Opaque UTF-8 string value (≤ 4096 chars).</param>
public sealed record EntityAttributeValueDto(
    string Id,
    string EntityType,
    string EntitySqid,
    string AttributeCode,
    string Value);

/// <summary>
/// R2190-R2200 / TOR §15.6 FLEX 006 — input DTO for the
/// <c>SetAsync</c> surface. The combination of (<c>EntityType</c>,
/// <c>EntitySqid</c>, <c>AttributeCode</c>) is naturally unique — the
/// service-side upsert path inserts on first observation and updates the
/// <c>Value</c> column on subsequent calls.
/// </summary>
/// <param name="EntityType">Logical entity kind (e.g. <c>Application</c>).</param>
/// <param name="EntitySqid">Sqid of the host entity row.</param>
/// <param name="AttributeCode">Stable attribute code (must be in the allow-list).</param>
/// <param name="Value">Opaque UTF-8 string value (≤ 4096 chars).</param>
public sealed record SetEntityAttributeInputDto(
    string EntityType,
    string EntitySqid,
    string AttributeCode,
    string Value);
