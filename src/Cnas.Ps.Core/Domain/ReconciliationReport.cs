namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2433 / TOR M4 — single row per <see cref="MigrationRun"/> capturing the
/// source-vs-target reconciliation outcome. Computed at the end of every
/// run (DryRun or Apply); the EF configuration enforces a unique index on
/// the <see cref="RunId"/> foreign key.
/// </summary>
/// <remarks>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because operators
/// reference a reconciliation by Sqid through the admin surface.
/// </para>
/// <para>
/// <b>No PII.</b> The <see cref="DiscrepancyDetailsJson"/> column lists at
/// most 100 deltas; each delta references only its opaque
/// <see cref="MigrationFinding.SourceFingerprint"/>-shaped hash.
/// </para>
/// </remarks>
public sealed class ReconciliationReport : AuditableEntity, IExternalId
{
    /// <summary>Foreign key to the parent <see cref="MigrationRun"/>.</summary>
    public long RunId { get; set; }

    /// <summary>Terminal outcome of the reconciliation.</summary>
    public ReconciliationStatus Status { get; set; }

    /// <summary>Source-side total row count (observed via <c>IMigrationSource.CountAsync</c>).</summary>
    public long SourceRowCount { get; set; }

    /// <summary>Target-side total row count (observed by querying <c>MigrationStagingRow</c> for the run).</summary>
    public long TargetRowCount { get; set; }

    /// <summary>Count of fingerprints present in the source but absent from the staging table.</summary>
    public long MissingInTargetCount { get; set; }

    /// <summary>Count of fingerprints present in the staging table but absent from the source.</summary>
    public long UnexpectedInTargetCount { get; set; }

    /// <summary>Fraction of source fingerprints that match a staging row (0..1, 4 decimal places).</summary>
    public decimal ChecksumMatchRate { get; set; }

    /// <summary>
    /// JSON array of up to 100 discrepancy descriptors. Each entry carries
    /// the PII-free fingerprint + kind ("missing" / "unexpected"). Null when
    /// no discrepancies were found. Bounded to 16384 characters.
    /// </summary>
    public string? DiscrepancyDetailsJson { get; set; }

    /// <summary>UTC instant the reconciliation was computed.</summary>
    public DateTime ComputedAt { get; set; }
}
