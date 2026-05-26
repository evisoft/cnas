namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2279 / TOR SEC 033 — one execution of the data-classification catalog
/// scan. Each row captures the trigger origin, the start timestamp (matches
/// <see cref="AuditableEntity.CreatedAtUtc"/> via the snapshot
/// <see cref="CapturedAt"/> column), the aggregated counters, and the
/// per-label distribution as a small JSON blob so operators can chart trends
/// from the dashboard without scanning the per-entry table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> The service creates a <c>Capturing</c> row at the start
/// of the scan with <c>TotalTypesScanned = 0</c>. On success the service
/// stamps the aggregated counters and flips the row to <c>Captured</c>. On
/// an unhandled exception the service marks the row <c>Failed</c> and
/// populates <see cref="FailureReason"/> for the operator post-mortem.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// outbound DTO (<c>Cnas.Ps.Contracts.ClassificationCatalogSnapshotDto.Id</c>)
/// carries a Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// <para>
/// <b>No PII.</b> The snapshot row stores classification metadata only — no
/// citizen attributes or business values are ever persisted here. The scanner
/// reads <see cref="System.Reflection.PropertyInfo"/> shapes and never the
/// actual property values.
/// </para>
/// </remarks>
public sealed class ClassificationCatalogSnapshot : AuditableEntity, IExternalId
{
    /// <summary>UTC timestamp the scan started (matches the <see cref="AuditableEntity.CreatedAtUtc"/> column).</summary>
    public DateTime CapturedAt { get; set; }

    /// <summary>Whether the snapshot was fired by the weekly scheduler or by an operator action.</summary>
    public ClassificationSnapshotTriggerKind TriggerKind { get; set; }

    /// <summary>Lifecycle status — defaults to <see cref="ClassificationSnapshotStatus.Capturing"/>.</summary>
    public ClassificationSnapshotStatus Status { get; set; } = ClassificationSnapshotStatus.Capturing;

    /// <summary>Total public Contracts types iterated by the scanner.</summary>
    public int TotalTypesScanned { get; set; }

    /// <summary>Total public properties on those types that carried an explicit <c>[SensitivityClassification]</c> attribute.</summary>
    public int TotalPropertiesClassified { get; set; }

    /// <summary>Total public properties that did NOT carry an explicit attribute (drift indicator).</summary>
    public int TotalPropertiesUnclassified { get; set; }

    /// <summary>
    /// JSON-serialised dictionary of label-name → property-count
    /// (e.g. <c>{"Public": 142, "Internal": 88, "Confidential": 26, "Restricted": 5}</c>).
    /// Null while the snapshot is in <see cref="ClassificationSnapshotStatus.Capturing"/>;
    /// populated atomically at completion.
    /// </summary>
    public string? LabelCountsJson { get; set; }

    /// <summary>
    /// JSON-serialised dictionary of <c>assemblyName → version</c> for every
    /// Contracts assembly the scanner enumerated. Lets future drift correlation
    /// distinguish "label changed" vs "assembly version bumped". Null until
    /// completion.
    /// </summary>
    public string? AssemblyVersionsJson { get; set; }

    /// <summary>
    /// Operator-facing reason populated when <see cref="Status"/> is
    /// <see cref="ClassificationSnapshotStatus.Failed"/>. Null otherwise. Capped at 1000 chars.
    /// </summary>
    public string? FailureReason { get; set; }
}
