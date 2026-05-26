using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Migration;

/// <summary>
/// R2430 / R2433 / TOR M4 — in-flight finding record emitted by an
/// <c>IMigrationRecordMapper</c>. The framework persists each one as a
/// <see cref="MigrationFinding"/> row, keyed to the current run, batch,
/// and row ordinal.
/// </summary>
/// <param name="Severity">Severity classification.</param>
/// <param name="FindingCode">Stable dot-separated code (≤ 64 chars).</param>
/// <param name="Description">PII-free human description (≤ 500 chars).</param>
public sealed record MigrationFindingRecord(
    MigrationFindingSeverity Severity,
    string FindingCode,
    string Description);

/// <summary>
/// R2431 / TOR M4 — mapped record produced by an
/// <c>IMigrationRecordMapper</c>. Carries the resolved target-entity key,
/// the JSON-encoded mapped fields, and any findings raised during
/// mapping. The framework persists each one as a
/// <see cref="MigrationStagingRow"/>.
/// </summary>
/// <param name="TargetEntityKey">Opaque natural key for the target row (≤ 256 chars).</param>
/// <param name="FieldsJson">JSON-encoded mapped fields ready for projection (≤ 16384 chars).</param>
/// <param name="Findings">Mapper-emitted findings (may be empty).</param>
public sealed record MigrationMappedRecord(
    string TargetEntityKey,
    string FieldsJson,
    System.Collections.Generic.IReadOnlyList<MigrationFindingRecord> Findings);
