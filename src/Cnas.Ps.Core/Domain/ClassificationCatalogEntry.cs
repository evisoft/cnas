namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2279 / TOR SEC 033 — one classified-property row inside a
/// <see cref="ClassificationCatalogSnapshot"/>. The natural key is
/// <c>(SnapshotId, TypeFullName, PropertyName)</c>; the
/// <see cref="ClassificationCatalogEntry"/> table is append-only — each
/// snapshot owns its own immutable set of rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>What "entry" captures.</b> An entry is the discovered (type, property,
/// label) tuple plus a boolean flagging whether the label came from an
/// explicit <c>SensitivityClassification</c> attribute or was defaulted
/// (the latter is a drift indicator — operators should add a label to the DTO).
/// </para>
/// <para>
/// <b>Label as a string.</b> The label is stored as the enum's name (e.g.
/// <c>"Public"</c>, <c>"Internal"</c>, <c>"Confidential"</c>, <c>"Restricted"</c>)
/// rather than the underlying integer because Core cannot take a dependency on
/// the <c>Cnas.Ps.Contracts.Security.SensitivityLabel</c> enum (Core has zero
/// outbound dependencies per CLAUDE.md §1.1). The Application layer maps the
/// string to/from the enum at the boundary.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// outbound DTO (<c>Cnas.Ps.Contracts.ClassificationCatalogEntryDto.Id</c>)
/// carries a Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// <para>
/// <b>Notes column.</b> Operators may pin a free-form note to an entry via a
/// future admin endpoint (e.g. "Public-by-design — see DSR-2026-04"). The
/// note is stored on the entry, not the snapshot, so it tracks the
/// (type, property) tuple rather than a particular scan instance.
/// </para>
/// </remarks>
public sealed class ClassificationCatalogEntry : AuditableEntity, IExternalId
{
    /// <summary>FK to the owning <see cref="ClassificationCatalogSnapshot"/>.</summary>
    public long SnapshotId { get; set; }

    /// <summary>
    /// Full CLR type name of the Contracts DTO (e.g. <c>Cnas.Ps.Contracts.UserGroupDto</c>).
    /// Capped at 512 characters.
    /// </summary>
    public required string TypeFullName { get; set; }

    /// <summary>Public property name on the DTO (e.g. <c>Code</c>). Capped at 128 characters.</summary>
    public required string PropertyName { get; set; }

    /// <summary>
    /// Effective sensitivity label resolved by the scanner, persisted as the
    /// enum name. Reuses the
    /// <c>Cnas.Ps.Contracts.Security.SensitivityLabel</c> enum from R0228 —
    /// see <c>SensitivityClassificationAttribute</c>. Capped at 32 characters.
    /// </summary>
    public required string Label { get; set; }

    /// <summary>
    /// True when the property carried an explicit
    /// <c>SensitivityClassification</c> attribute; false when the scanner
    /// defaulted the label (drift indicator).
    /// </summary>
    public bool IsExplicit { get; set; }

    /// <summary>
    /// Simple name of the assembly that declares the property (e.g. <c>Cnas.Ps.Contracts</c>).
    /// Capped at 128 characters.
    /// </summary>
    public required string DeclaringAssembly { get; set; }

    /// <summary>
    /// Optional operator-supplied note pinned to this (type, property, snapshot) tuple.
    /// Capped at 500 characters; null on insertion, populated by a separate admin endpoint.
    /// </summary>
    public string? Notes { get; set; }
}
