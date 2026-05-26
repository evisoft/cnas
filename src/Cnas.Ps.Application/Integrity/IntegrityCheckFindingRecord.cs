using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Integrity;

/// <summary>
/// R2282 / TOR SEC 036 — value object returned by an
/// <see cref="IIntegrityCheck"/> for each invariant violation it detects.
/// The job consumes the record and writes a persistent
/// <see cref="IntegrityCheckFinding"/> row.
/// </summary>
/// <param name="CheckCode">Stable check code (mirrors the originating <c>IIntegrityCheck.CheckCode</c>).</param>
/// <param name="Severity">Per-finding severity (typically constant per check, but checks may override).</param>
/// <param name="AggregateName">Display name of the offending aggregate (e.g. <c>Claim</c>).</param>
/// <param name="AggregateRowId">Raw bigint PK of the offending row.</param>
/// <param name="Description">Human-readable description of the violation; PII-free.</param>
/// <param name="ExpectedValue">Expected value per the invariant rule, when known.</param>
/// <param name="ActualValue">Actual value observed at scan time, when known.</param>
public sealed record IntegrityCheckFindingRecord(
    string CheckCode,
    IntegrityFindingSeverity Severity,
    string AggregateName,
    long AggregateRowId,
    string Description,
    string? ExpectedValue,
    string? ActualValue);

/// <summary>
/// R2282 / TOR SEC 036 — outcome envelope returned by
/// <see cref="IIntegrityCheck.RunAsync"/>. Carries the scanned-row count
/// and the list of findings the check produced.
/// </summary>
/// <param name="RowsScanned">Number of rows the check examined during this run.</param>
/// <param name="Findings">Findings produced by the check (may be empty).</param>
public sealed record IntegrityCheckPartialResult(
    long RowsScanned,
    IReadOnlyList<IntegrityCheckFindingRecord> Findings);
