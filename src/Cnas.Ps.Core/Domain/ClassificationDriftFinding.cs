namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2279 / TOR SEC 033 — one drift detection between two
/// <see cref="ClassificationCatalogSnapshot"/> rows (baseline → current).
/// Operators acknowledge a finding once they have reviewed the change.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency.</b> The natural key
/// (<see cref="BaselineSnapshotId"/>, <see cref="CurrentSnapshotId"/>,
/// <see cref="TypeFullName"/>, <see cref="PropertyName"/>, <see cref="DriftKind"/>)
/// is enforced by a composite unique index in
/// <c>ClassificationDriftFindingConfiguration</c>. A re-run of the drift
/// computation against the same pair returns the existing rows without
/// re-inserting.
/// </para>
/// <para>
/// <b>Acknowledgement.</b> An operator may acknowledge a finding once
/// investigated. The acknowledgement carries a note (free-form, 3..1000
/// chars) and the acknowledging user's id. The underlying drift is NOT
/// repaired by acknowledgement — acknowledgement is operator-bookkeeping,
/// not a code-level fix.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// outbound DTO (<c>Cnas.Ps.Contracts.ClassificationDriftFindingDto.Id</c>)
/// carries a Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
public sealed class ClassificationDriftFinding : AuditableEntity, IExternalId
{
    /// <summary>FK to the baseline <see cref="ClassificationCatalogSnapshot"/> the comparison started from.</summary>
    public long BaselineSnapshotId { get; set; }

    /// <summary>FK to the current <see cref="ClassificationCatalogSnapshot"/> that introduced the change.</summary>
    public long CurrentSnapshotId { get; set; }

    /// <summary>Kind of drift detected (Added / Removed / LabelChanged / ClassificationLost).</summary>
    public ClassificationDriftKind DriftKind { get; set; }

    /// <summary>Full CLR type name (e.g. <c>Cnas.Ps.Contracts.UserGroupDto</c>). Capped at 512 characters.</summary>
    public required string TypeFullName { get; set; }

    /// <summary>Public property name on the DTO. Capped at 128 characters.</summary>
    public required string PropertyName { get; set; }

    /// <summary>
    /// Label name on the baseline snapshot (null when the property did not
    /// exist on the baseline — e.g. <see cref="ClassificationDriftKind.Added"/>).
    /// Capped at 32 characters.
    /// </summary>
    public string? BaselineLabel { get; set; }

    /// <summary>
    /// Label name on the current snapshot (null when the property has been
    /// removed in the current snapshot — e.g. <see cref="ClassificationDriftKind.Removed"/>).
    /// Capped at 32 characters.
    /// </summary>
    public string? CurrentLabel { get; set; }

    /// <summary>Whether an operator has acknowledged the finding.</summary>
    public bool Acknowledged { get; set; }

    /// <summary>FK to the <see cref="UserProfile"/> who acknowledged the finding.</summary>
    public long? AcknowledgedByUserId { get; set; }

    /// <summary>UTC timestamp of the acknowledgement, when applicable.</summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>
    /// Free-form note accompanying the acknowledgement (3..1000 chars when
    /// set; null while the finding is unacknowledged). Capped at 1000 characters.
    /// </summary>
    public string? AcknowledgementNote { get; set; }

    /// <summary>UTC timestamp the drift was first detected (matches <see cref="AuditableEntity.CreatedAtUtc"/>).</summary>
    public DateTime DetectedAt { get; set; }
}
