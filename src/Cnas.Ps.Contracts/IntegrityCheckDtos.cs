using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R2282 / TOR SEC 036 — row-integrity check jobs DTOs
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R2282 / TOR SEC 036 — one execution of the data-integrity sweep as it
/// leaves the system. Operational ops data — all fields carry the
/// <c>Internal</c> sensitivity label (no PII).
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying run row.</param>
/// <param name="RunStartedAt">UTC timestamp the run started.</param>
/// <param name="RunCompletedAt">UTC timestamp the run completed; null while in flight.</param>
/// <param name="TriggerKind">Stable enum-name representation of the trigger origin (<c>Scheduled</c> / <c>Manual</c>).</param>
/// <param name="Status">Stable enum-name representation of the lifecycle status (<c>Running</c> / <c>Completed</c> / <c>Failed</c>).</param>
/// <param name="TotalRowsScanned">Total rows scanned across every check.</param>
/// <param name="TotalFindings">Total findings recorded.</param>
/// <param name="FindingsBySeverity">Severity-name → finding-count map; empty while the run is in flight.</param>
/// <param name="FailureReason">Operator-facing reason when status is <c>Failed</c>; null otherwise.</param>
public sealed record IntegrityCheckRunDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime RunStartedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime? RunCompletedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string TriggerKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    long TotalRowsScanned,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int TotalFindings,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyDictionary<string, int> FindingsBySeverity,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? FailureReason);

/// <summary>
/// R2282 / TOR SEC 036 — one detected invariant violation as it leaves the
/// system.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="AggregateRowId"/> is raw.</b> The integrity-check admin
/// surface is an INTERNAL ops dashboard. Operators MUST be able to use this
/// raw bigint to locate the offending row via dump tools, ad-hoc SQL, and the
/// EF Designer — round-tripping it through Sqids would defeat that purpose.
/// This is the ONLY documented exception to CLAUDE.md RULE 3 in the codebase.
/// The companion <see cref="AggregateName"/> field disambiguates the target
/// table; together the pair is unambiguous. Sensitivity is <c>Internal</c>
/// because the row id itself is not PII — the underlying data may be,
/// but the finding payload does not carry it.
/// </para>
/// </remarks>
/// <param name="Id">Sqid-encoded surrogate id of the finding row.</param>
/// <param name="RunSqid">Sqid-encoded id of the parent <c>IntegrityCheckRun</c>.</param>
/// <param name="CheckCode">Stable, screaming-snake-case identifier for the failed invariant.</param>
/// <param name="Severity">Stable enum-name representation of the severity (<c>Critical</c> / <c>High</c> / <c>Medium</c> / <c>Low</c>).</param>
/// <param name="AggregateName">Display name of the offending aggregate (e.g. <c>Claim</c>, <c>ExecutoryDocument</c>).</param>
/// <param name="AggregateRowId">Raw bigint PK of the offending row — see remarks for the rationale of the raw-id exception.</param>
/// <param name="Description">Human-readable explanation of the violation; never contains PII.</param>
/// <param name="ExpectedValue">Expected value per the invariant rule, when known.</param>
/// <param name="ActualValue">Actual value observed at scan time, when known.</param>
/// <param name="FirstDetectedAt">UTC timestamp the finding was inserted.</param>
/// <param name="Acknowledged">Whether an operator has acknowledged the finding.</param>
/// <param name="AcknowledgedAt">UTC timestamp of the acknowledgement, when applicable.</param>
/// <param name="AcknowledgedByUserSqid">Sqid-encoded id of the acknowledging user, when applicable.</param>
/// <param name="AcknowledgementNote">Free-form note accompanying the acknowledgement (3..1000 chars when set).</param>
public sealed record IntegrityCheckFindingDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string RunSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string CheckCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Severity,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string AggregateName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    long AggregateRowId,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Description,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ExpectedValue,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ActualValue,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime FirstDetectedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool Acknowledged,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime? AcknowledgedAt,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? AcknowledgedByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? AcknowledgementNote);

/// <summary>
/// R2282 / TOR SEC 036 — run summary plus the full list of findings recorded
/// during the run. Returned by the per-run detail endpoint.
/// </summary>
/// <param name="Run">Run summary header.</param>
/// <param name="Findings">Findings recorded during the run, in insertion order.</param>
public sealed record IntegrityCheckRunDetailsDto(
    IntegrityCheckRunDto Run,
    IReadOnlyList<IntegrityCheckFindingDto> Findings);

/// <summary>
/// R2282 / TOR SEC 036 — filter envelope for the open-findings list endpoint.
/// </summary>
/// <param name="Severity">Optional stable enum-name filter — null returns all severities.</param>
/// <param name="AggregateName">Optional aggregate-name filter — null returns all aggregates.</param>
/// <param name="CheckCode">Optional check-code filter — null returns all codes.</param>
/// <param name="OnlyOpen">When true (default), only un-acknowledged findings are returned.</param>
/// <param name="Skip">Page offset; ≥ 0.</param>
/// <param name="Take">Page size; 1..200.</param>
public sealed record IntegrityFindingFilterDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Severity,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? AggregateName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CheckCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool OnlyOpen = true,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip = 0,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take = 50);

/// <summary>
/// R2282 / TOR SEC 036 — paged response envelope for the open-findings list
/// endpoint.
/// </summary>
/// <param name="Items">Findings page.</param>
/// <param name="Total">Total matching findings across all pages.</param>
/// <param name="Skip">Echoed page offset.</param>
/// <param name="Take">Echoed page size.</param>
public sealed record IntegrityFindingPageDto(
    IReadOnlyList<IntegrityCheckFindingDto> Items,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Total,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take);

/// <summary>
/// R2282 / TOR SEC 036 — acknowledgement payload for a finding.
/// </summary>
/// <param name="Note">Operator-supplied investigation note (3..1000 chars).</param>
public sealed record IntegrityFindingAcknowledgeInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Note);
