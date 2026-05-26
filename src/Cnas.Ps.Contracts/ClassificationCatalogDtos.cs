using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R2279 / TOR SEC 033 — data-classification catalog DTOs (operational ops data)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R2279 / TOR SEC 033 — one classification-catalog snapshot as it leaves the
/// system. Pure operational ops data — every field carries the
/// <see cref="SensitivityLabel.Internal"/> label except the Sqid id, which is
/// <see cref="SensitivityLabel.Public"/>. No citizen-attribute values are
/// ever persisted in the catalog so the wire-DTO can never accidentally leak PII.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying snapshot row.</param>
/// <param name="CapturedAt">UTC timestamp the scan started.</param>
/// <param name="TriggerKind">Stable enum-name representation of the trigger origin (<c>Scheduled</c> / <c>Manual</c>).</param>
/// <param name="Status">Stable enum-name representation of the lifecycle status (<c>Capturing</c> / <c>Captured</c> / <c>Failed</c>).</param>
/// <param name="TotalTypesScanned">Total Contracts types iterated by the scan.</param>
/// <param name="TotalPropertiesClassified">Properties that carried an explicit <c>[SensitivityClassification]</c>.</param>
/// <param name="TotalPropertiesUnclassified">Properties without an explicit attribute (drift indicator).</param>
/// <param name="LabelCounts">Label-name → property-count map; empty while the snapshot is in flight.</param>
/// <param name="AssemblyVersions">Assembly-simple-name → version map for the scanned Contracts assemblies.</param>
/// <param name="FailureReason">Operator-facing reason when status is <c>Failed</c>; null otherwise.</param>
public sealed record ClassificationCatalogSnapshotDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime CapturedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string TriggerKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int TotalTypesScanned,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int TotalPropertiesClassified,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int TotalPropertiesUnclassified,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyDictionary<string, int> LabelCounts,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyDictionary<string, string> AssemblyVersions,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? FailureReason);

/// <summary>
/// R2279 / TOR SEC 033 — one classified-property entry as it leaves the
/// system. Pure operational ops data — every field carries the
/// <see cref="SensitivityLabel.Internal"/> label except the Sqids, which are
/// <see cref="SensitivityLabel.Public"/>.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the entry row.</param>
/// <param name="SnapshotSqid">Sqid-encoded id of the parent snapshot.</param>
/// <param name="TypeFullName">Full CLR type name of the Contracts DTO.</param>
/// <param name="PropertyName">Public property name on the DTO.</param>
/// <param name="Label">Stable enum-name representation of the effective sensitivity label.</param>
/// <param name="IsExplicit">True when an explicit <c>[SensitivityClassification]</c> attribute was found.</param>
/// <param name="DeclaringAssembly">Simple name of the assembly that declared the property.</param>
/// <param name="Notes">Optional operator-pinned note (≤ 500 chars); null on insertion.</param>
public sealed record ClassificationCatalogEntryDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string SnapshotSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string TypeFullName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string PropertyName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Label,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool IsExplicit,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string DeclaringAssembly,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes);

/// <summary>
/// R2279 / TOR SEC 033 — snapshot summary plus a paged list of the snapshot's
/// per-property entries. Returned by the per-snapshot details endpoint.
/// </summary>
/// <param name="Snapshot">Snapshot summary header.</param>
/// <param name="Entries">Page of entries that match the supplied filter.</param>
/// <param name="Total">Total entry count matching the filter across all pages.</param>
/// <param name="Skip">Echoed page offset.</param>
/// <param name="Take">Echoed page size.</param>
public sealed record ClassificationCatalogSnapshotDetailsDto(
    ClassificationCatalogSnapshotDto Snapshot,
    IReadOnlyList<ClassificationCatalogEntryDto> Entries,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Total,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take);

/// <summary>
/// R2279 / TOR SEC 033 — filter envelope for the snapshot-details endpoint.
/// </summary>
/// <param name="Label">Optional stable label name (<c>Public</c> / <c>Internal</c> / <c>Confidential</c> / <c>Restricted</c>) — null matches any label.</param>
/// <param name="IsExplicit">Optional explicit-attribute filter — null matches both.</param>
/// <param name="TypeFullNameContains">Optional case-sensitive substring filter on the type full name.</param>
/// <param name="Skip">Page offset; ≥ 0.</param>
/// <param name="Take">Page size; 1..500.</param>
public sealed record ClassificationCatalogEntryFilterDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Label,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool? IsExplicit,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? TypeFullNameContains,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip = 0,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take = 100);

/// <summary>
/// R2279 / TOR SEC 033 — paged response envelope for the snapshot-list endpoint.
/// </summary>
/// <param name="Items">Snapshots page, most-recent first.</param>
/// <param name="Total">Total snapshot count across all pages.</param>
public sealed record ClassificationCatalogSnapshotPageDto(
    IReadOnlyList<ClassificationCatalogSnapshotDto> Items,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Total);

/// <summary>
/// R2279 / TOR SEC 033 — per-property tuple returned by
/// <c>IClassificationCatalogScanner.ScanAsync</c>. Pure transport — the
/// service projects this list into <see cref="ClassificationCatalogEntryDto"/>
/// rows on the snapshot.
/// </summary>
/// <param name="TypeFullName">Full CLR type name (e.g. <c>Cnas.Ps.Contracts.UserGroupDto</c>).</param>
/// <param name="PropertyName">Public property name (e.g. <c>Code</c>).</param>
/// <param name="Label">Stable enum-name of the effective sensitivity label.</param>
/// <param name="IsExplicit">True when the property carried an explicit attribute.</param>
/// <param name="DeclaringAssembly">Simple name of the declaring assembly.</param>
public sealed record ScannedPropertyDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string TypeFullName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string PropertyName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Label,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool IsExplicit,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string DeclaringAssembly);

/// <summary>
/// R2279 / TOR SEC 033 — outcome of a fresh scanner pass. Pure read; not
/// persisted directly. The catalog service projects this into a
/// <see cref="ClassificationCatalogSnapshotDto"/> + per-property entry rows.
/// </summary>
/// <param name="TotalTypesScanned">Total public Contracts types iterated.</param>
/// <param name="TotalPropertiesClassified">Properties with an explicit attribute.</param>
/// <param name="TotalPropertiesUnclassified">Properties without an explicit attribute.</param>
/// <param name="Properties">Per-property entries in deterministic order.</param>
/// <param name="LabelCounts">Label-name → property-count map.</param>
/// <param name="AssemblyVersions">Assembly-simple-name → version map.</param>
public sealed record ClassificationCatalogScanOutcomeDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int TotalTypesScanned,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int TotalPropertiesClassified,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int TotalPropertiesUnclassified,
    IReadOnlyList<ScannedPropertyDto> Properties,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyDictionary<string, int> LabelCounts,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyDictionary<string, string> AssemblyVersions);

/// <summary>
/// R2279 / TOR SEC 033 — drift-detection envelope between two snapshots.
/// </summary>
/// <param name="BaselineSnapshotSqid">Sqid of the baseline snapshot.</param>
/// <param name="CurrentSnapshotSqid">Sqid of the current snapshot.</param>
/// <param name="FindingsCount">Total drift findings persisted for the pair.</param>
/// <param name="Findings">Drift findings — page subset.</param>
public sealed record ClassificationDriftResultDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string BaselineSnapshotSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string CurrentSnapshotSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int FindingsCount,
    IReadOnlyList<ClassificationDriftFindingDto> Findings);

/// <summary>
/// R2279 / TOR SEC 033 — one drift-finding row as it leaves the system.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the finding row.</param>
/// <param name="BaselineSnapshotSqid">Sqid of the baseline snapshot.</param>
/// <param name="CurrentSnapshotSqid">Sqid of the current snapshot.</param>
/// <param name="DriftKind">Stable enum-name (<c>Added</c> / <c>Removed</c> / <c>LabelChanged</c> / <c>ClassificationLost</c>).</param>
/// <param name="TypeFullName">Full CLR type name (e.g. <c>Cnas.Ps.Contracts.UserGroupDto</c>).</param>
/// <param name="PropertyName">Public property name on the DTO.</param>
/// <param name="BaselineLabel">Label name on the baseline snapshot; null on Added.</param>
/// <param name="CurrentLabel">Label name on the current snapshot; null on Removed.</param>
/// <param name="Acknowledged">Whether the finding has been acknowledged.</param>
/// <param name="AcknowledgedAt">UTC timestamp of the acknowledgement, when applicable.</param>
/// <param name="AcknowledgementNote">Free-form acknowledgement note (3..1000 chars when set).</param>
/// <param name="DetectedAt">UTC timestamp the drift was first detected.</param>
public sealed record ClassificationDriftFindingDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string BaselineSnapshotSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string CurrentSnapshotSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string DriftKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string TypeFullName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string PropertyName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? BaselineLabel,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CurrentLabel,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool Acknowledged,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime? AcknowledgedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? AcknowledgementNote,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime DetectedAt);

/// <summary>
/// R2279 / TOR SEC 033 — filter envelope for the drift-list endpoint.
/// </summary>
/// <param name="DriftKind">Optional drift-kind name filter — null matches any kind.</param>
/// <param name="Acknowledged">Optional acknowledgement-state filter — null matches both.</param>
/// <param name="Skip">Page offset; ≥ 0.</param>
/// <param name="Take">Page size; 1..200.</param>
public sealed record ClassificationDriftFilterDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? DriftKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool? Acknowledged,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip = 0,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take = 50);

/// <summary>
/// R2279 / TOR SEC 033 — paged response envelope for the drift-list endpoint.
/// </summary>
/// <param name="Items">Findings page.</param>
/// <param name="Total">Total matching findings across all pages.</param>
/// <param name="Skip">Echoed page offset.</param>
/// <param name="Take">Echoed page size.</param>
public sealed record ClassificationDriftPageDto(
    IReadOnlyList<ClassificationDriftFindingDto> Items,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Total,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take);

/// <summary>
/// R2279 / TOR SEC 033 — acknowledgement payload for a drift finding.
/// </summary>
/// <param name="Note">Operator-supplied investigation note (3..1000 chars).</param>
public sealed record ClassificationDriftAcknowledgeInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Note);
