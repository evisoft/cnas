namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2190-R2200 / TOR §15.6 FLEX 006 — dynamic entity attribute (EAV-style
/// sidecar row) used to extend existing core entities without a migration.
/// One row per (<see cref="EntityType"/>, <see cref="EntityId"/>,
/// <see cref="AttributeCode"/>) tuple holds a flexible UTF-8 string value
/// that the application layer interprets according to a small allow-list.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> This is a deliberately tiny EAV sidecar — it does NOT replace
/// proper schema evolution for first-class business attributes. It is the
/// configurable surface that lets functional administrators tag rows with
/// supplementary metadata (priority, tag set, free-form labels) without a
/// roll-out window. Anything that needs validation richer than "string ≤ 4096
/// chars" still belongs on the main entity.
/// </para>
/// <para>
/// <b>Allow-list.</b> The set of valid <see cref="AttributeCode"/> values is
/// pinned by the service layer (see <c>IDynamicAttributeService</c>) and
/// rejected at the boundary. The DB column accepts any 1-64 character ASCII
/// code by design — the deny-by-default policy lives one layer up so
/// per-tenant allow-lists become possible later without a migration.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> so future admin /
/// audit surfaces can list per-entity attribute sets via Sqid-encoded ids.
/// </para>
/// </remarks>
public sealed class EntityAttributeValue : AuditableEntity, IExternalId
{
    /// <summary>
    /// Logical kind of entity this attribute hangs off (e.g. <c>Application</c>,
    /// <c>Decision</c>, <c>ServicePassport</c>). Stable string code — never an
    /// enum, so adding a new host entity does not require a CLR-side migration.
    /// </summary>
    public required string EntityType { get; set; }

    /// <summary>
    /// Internal 64-bit primary key of the host entity row. Pairs with
    /// <see cref="EntityType"/> to identify a specific entity instance.
    /// </summary>
    public long EntityId { get; set; }

    /// <summary>
    /// Stable attribute code — the "name" of the EAV cell. Allowed values are
    /// pinned by the service layer; the column accepts any 1-64 ASCII string.
    /// </summary>
    public required string AttributeCode { get; set; }

    /// <summary>
    /// UTF-8 string value (≤ 4096 chars). The application layer interprets it
    /// according to the attribute code — most consumers treat it as opaque
    /// text. Never holds PII; the audit subsystem strips this column from
    /// every dump for safety.
    /// </summary>
    public required string Value { get; set; }
}
