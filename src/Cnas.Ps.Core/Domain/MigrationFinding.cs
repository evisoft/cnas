namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2430 / R2433 / TOR M4 — one row per data-quality / transformation issue
/// encountered during a <see cref="MigrationRun"/>. Findings are PII-free —
/// the offending source row is referenced exclusively via its opaque
/// <see cref="SourceFingerprint"/>, never via raw column values.
/// </summary>
/// <remarks>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because operators
/// reference an individual finding by Sqid through the admin surface.
/// </para>
/// <para>
/// <b>Acknowledgement workflow.</b> Findings default to
/// <see cref="Acknowledged"/>=false. The acknowledge endpoint records the
/// operator who reviewed it, the UTC instant, and a free-form note bounded
/// to 1000 characters. Acknowledged findings remain visible — they are
/// never hard-deleted.
/// </para>
/// </remarks>
public sealed class MigrationFinding : AuditableEntity, IExternalId
{
    /// <summary>Foreign key to the parent <see cref="MigrationRun"/>.</summary>
    public long RunId { get; set; }

    /// <summary>1-based ordinal of the batch in which the finding was raised.</summary>
    public int BatchOrdinal { get; set; }

    /// <summary>0-based row ordinal within the batch.</summary>
    public int RowOrdinalInBatch { get; set; }

    /// <summary>Severity classification of the finding.</summary>
    public MigrationFindingSeverity Severity { get; set; }

    /// <summary>
    /// Stable, dot-separated finding code, e.g. <c>PII.MISSING_IDNP</c>,
    /// <c>MAPPING.UNKNOWN_TYPE</c>, <c>FK.UNRESOLVED</c>. Bounded to 64
    /// characters.
    /// </summary>
    public string FindingCode { get; set; } = string.Empty;

    /// <summary>Short, PII-free human description bounded to 500 characters.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Opaque hash/identifier of the offending source row. Typically a
    /// SHA-256 of the canonicalised row content. Bounded to 128 characters.
    /// </summary>
    public string SourceFingerprint { get; set; } = string.Empty;

    /// <summary>True once an operator acknowledges the finding.</summary>
    public bool Acknowledged { get; set; }

    /// <summary>UTC instant the finding was acknowledged. Null while unacknowledged.</summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>Internal id of the operator who acknowledged the finding. Null while unacknowledged.</summary>
    public long? AcknowledgedByUserId { get; set; }

    /// <summary>Operator-supplied acknowledgement note bounded to 1000 characters.</summary>
    public string? AcknowledgementNote { get; set; }
}
