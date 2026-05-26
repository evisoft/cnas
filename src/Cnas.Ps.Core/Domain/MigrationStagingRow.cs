namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2431 / TOR M4 — generic staging row produced by the migration importer
/// for every mapped source record. The row carries the JSON-encoded mapped
/// fields plus the opaque target-entity key; a future per-entity commit
/// step (out of scope this iteration) will project committed staging rows
/// into the real target aggregates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Confidentiality.</b> <see cref="MappedFieldsJson"/> may carry PII for
/// legitimate migration work (e.g. IDNP digits captured from the legacy
/// system). The row is therefore treated as Confidential at the API
/// boundary — admin DTOs that surface it must be classified Internal /
/// Confidential and the admin surface refuses anonymous access.
/// </para>
/// <para>
/// <b>DryRun vs Apply.</b> Both modes persist staging rows. DryRun rows
/// remain <see cref="IsCommitted"/>=false forever; Apply rows are flipped
/// to <see cref="IsCommitted"/>=true once the mapper signals validation
/// success.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because admins
/// can peek individual staging rows by Sqid for forensic replay.
/// </para>
/// </remarks>
public sealed class MigrationStagingRow : AuditableEntity, IExternalId
{
    /// <summary>Foreign key to the parent <see cref="MigrationRun"/>.</summary>
    public long RunId { get; set; }

    /// <summary>1-based ordinal of the batch this row belongs to.</summary>
    public int BatchOrdinal { get; set; }

    /// <summary>0-based ordinal of the row inside its batch.</summary>
    public int RowOrdinalInBatch { get; set; }

    /// <summary>
    /// Symbolic name of the target aggregate, copied from the parent plan's
    /// <see cref="MigrationPlan.TargetEntityName"/>. Bounded to 128 characters.
    /// </summary>
    public string TargetEntityName { get; set; } = string.Empty;

    /// <summary>
    /// Opaque key into the target system — typically the natural-key value
    /// (e.g. IDNP for a Solicitant). Bounded to 256 characters.
    /// </summary>
    public string TargetEntityKey { get; set; } = string.Empty;

    /// <summary>
    /// JSON encoding of the mapped fields ready for projection into the
    /// real target aggregate. Bounded to 16384 characters. MAY contain PII
    /// — see remarks about Confidentiality.
    /// </summary>
    public string MappedFieldsJson { get; set; } = string.Empty;

    /// <summary>
    /// Opaque hash/identifier of the original source row this staging row
    /// derives from. Used by the reconciler to compute source-vs-target
    /// deltas. Bounded to 128 characters.
    /// </summary>
    public string SourceFingerprint { get; set; } = string.Empty;

    /// <summary>True once the row has been committed to the target system.</summary>
    public bool IsCommitted { get; set; }

    /// <summary>UTC instant the row was committed; null while pending.</summary>
    public DateTime? CommittedAt { get; set; }
}
