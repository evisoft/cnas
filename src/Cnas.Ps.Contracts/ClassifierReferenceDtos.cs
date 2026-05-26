using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0402 / TOR CF 17.09 — classifier-row reference-blocking DTOs. Surfaced by
// the IClassifierReferenceGuard pre-flight check that runs before a
// deactivation / deletion of a classifier row is allowed. The shape is
// depersonalised — only entity-name + row-count tuples, never the referencing
// rows themselves.
//
// Sensitivity: Internal — operator-facing only, never published to anonymous
// or public surfaces. Contracts MUST NOT <see cref="…"/> into Cnas.Ps.Core
// per project rules.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0402 / TOR CF 17.09 — depersonalised summary of how many rows in which
/// other entities currently reference the (scheme, code) pair the caller is
/// about to deactivate or delete.
/// </summary>
/// <param name="SchemeCode">Stable classifier kind/scheme code (e.g. <c>CAEM</c>).</param>
/// <param name="Value">Classifier code value within the scheme (e.g. <c>01.11</c>).</param>
/// <param name="ReferencingRowCount">Total count across every entity below.</param>
/// <param name="ReferencingEntities">Per-entity breakdown of the citing rows.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record ClassifierReferenceScanResultDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string SchemeCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Value,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long ReferencingRowCount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<ClassifierReferencingEntityDto> ReferencingEntities);

/// <summary>
/// R0402 / TOR CF 17.09 — one (entity-name, count) tuple emitted by the
/// reference-guard. <paramref name="EntityName"/> is the CLR class simple
/// name (e.g. <c>Contributor</c>) so operators can locate the table in
/// docs without leaking schema-internal identifiers.
/// </summary>
/// <param name="EntityName">Simple name of the citing entity (e.g. <c>Contributor</c>).</param>
/// <param name="Count">Number of rows in that entity referencing the value.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record ClassifierReferencingEntityDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string EntityName,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long Count);
