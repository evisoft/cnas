using System;
using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R2430 / R2431 / R2433 / TOR M4 — DTOs for the migration framework: plan
// registry, run records, batch counters, findings, reconciliation report,
// and staging-row peek envelope. All <c>Id</c> fields are Sqid-encoded per
// CLAUDE.md RULE 3.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R2430 / TOR M4 — outbound projection of a migration plan.
/// </summary>
/// <param name="Id">Sqid-encoded plan id.</param>
/// <param name="PlanCode">Stable SCREAMING_SNAKE_CASE plan code.</param>
/// <param name="Title">Human-readable title.</param>
/// <param name="Description">Optional free-form description.</param>
/// <param name="SourceKind">Stable enum-name of the source kind.</param>
/// <param name="TargetEntityName">Symbolic name of the destination aggregate.</param>
/// <param name="MappingDescriptorJson">Opaque mapping JSON; may be null.</param>
/// <param name="BatchSize">Rows-per-batch knob.</param>
/// <param name="Status">Stable enum-name of the lifecycle status.</param>
/// <param name="RegisteredByUserSqid">Sqid of the operator who registered the plan.</param>
/// <param name="ApprovedByUserSqid">Sqid of the operator who approved the plan; null while in Draft.</param>
/// <param name="ApprovedAt">UTC instant the plan was approved; null while in Draft.</param>
/// <param name="CreatedAtUtc">UTC instant the plan was created.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MigrationPlanDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string PlanCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Title,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string SourceKind,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string TargetEntityName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? MappingDescriptorJson,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int BatchSize,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RegisteredByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ApprovedByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? ApprovedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime CreatedAtUtc);

/// <summary>R2430 / TOR M4 — input envelope for creating a migration plan.</summary>
/// <param name="PlanCode">Stable SCREAMING_SNAKE_CASE code (pattern <c>^[A-Z][A-Z0-9_.]{1,63}$</c>).</param>
/// <param name="Title">Human-readable title (3..256 chars).</param>
/// <param name="Description">Optional free-form description (≤ 2000 chars).</param>
/// <param name="SourceKind">Stable enum-name string of the source kind.</param>
/// <param name="TargetEntityName">Symbolic destination aggregate name (≤ 128 chars).</param>
/// <param name="MappingDescriptorJson">Opaque JSON mapping descriptor; may be null.</param>
/// <param name="BatchSize">Rows-per-batch knob (10..10000; default 1000).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record MigrationPlanCreateInputDto(
    string PlanCode,
    string Title,
    string? Description,
    string SourceKind,
    string TargetEntityName,
    string? MappingDescriptorJson,
    int BatchSize = 1000);

/// <summary>R2430 / TOR M4 — input envelope for modifying a Draft migration plan.</summary>
/// <param name="Title">Human-readable title (3..256 chars).</param>
/// <param name="Description">Optional free-form description (≤ 2000 chars).</param>
/// <param name="MappingDescriptorJson">Opaque JSON mapping descriptor.</param>
/// <param name="BatchSize">Rows-per-batch knob (10..10000).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record MigrationPlanModifyInputDto(
    string Title,
    string? Description,
    string? MappingDescriptorJson,
    int BatchSize);

/// <summary>R2430 / TOR M4 — input envelope for transitions that need a reason (suspend / archive).</summary>
/// <param name="Reason">Free-form reason (3..1000 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MigrationPlanReasonInputDto(string Reason);

/// <summary>R2430 / TOR M4 — filter envelope for the plans list endpoint.</summary>
/// <param name="Status">Optional lifecycle-status filter (stable enum-name).</param>
/// <param name="TargetEntityName">Optional destination-aggregate filter.</param>
/// <param name="Skip">Page offset (default 0; must be ≥ 0).</param>
/// <param name="Take">Page size (default 50; max 100).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record MigrationPlanFilterDto(
    string? Status = null,
    string? TargetEntityName = null,
    int Skip = 0,
    int Take = 50);

/// <summary>R2430 / TOR M4 — paged envelope returned by the plans list endpoint.</summary>
/// <param name="Items">Plans on the requested page.</param>
/// <param name="Total">Total matching plans across all pages.</param>
/// <param name="Skip">Page offset that was applied.</param>
/// <param name="Take">Page size that was applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MigrationPlanPageDto(
    IReadOnlyList<MigrationPlanDto> Items,
    int Total,
    int Skip,
    int Take);

/// <summary>R2430 / R2431 / TOR M4 — outbound projection of a migration run.</summary>
/// <param name="Id">Sqid-encoded run id.</param>
/// <param name="PlanSqid">Sqid-encoded parent plan id.</param>
/// <param name="TriggerKind">Stable enum-name of the trigger origin.</param>
/// <param name="Status">Stable enum-name of the current run status.</param>
/// <param name="StartedAt">UTC instant the run started.</param>
/// <param name="CompletedAt">UTC instant the run finalised; null while in flight.</param>
/// <param name="TotalSourceRowsSeen">Total source rows observed.</param>
/// <param name="TotalRowsImported">Count of staging rows persisted as Imported.</param>
/// <param name="TotalRowsUpdated">Count of staging rows persisted as Updated.</param>
/// <param name="TotalRowsSkipped">Count of rows the mapper deemed redundant.</param>
/// <param name="TotalRowsFailed">Count of source rows that failed mapping.</param>
/// <param name="FailureReason">Sanitised PII-free failure reason; null on success.</param>
/// <param name="IsDryRun">True when the run was a DryRun (no commit).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MigrationRunDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string PlanSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string TriggerKind,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime StartedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? CompletedAt,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalSourceRowsSeen,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalRowsImported,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalRowsUpdated,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalRowsSkipped,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalRowsFailed,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? FailureReason,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsDryRun);

/// <summary>R2431 / TOR M4 — compact summary returned right after a run finalises.</summary>
/// <param name="Id">Sqid-encoded run id.</param>
/// <param name="PlanSqid">Sqid-encoded parent plan id.</param>
/// <param name="Status">Stable enum-name of the terminal status.</param>
/// <param name="TriggerKind">Stable enum-name of the trigger origin.</param>
/// <param name="TotalSourceRowsSeen">Total source rows observed.</param>
/// <param name="TotalRowsImported">Count of Imported rows.</param>
/// <param name="TotalRowsUpdated">Count of Updated rows.</param>
/// <param name="TotalRowsSkipped">Count of Skipped rows.</param>
/// <param name="TotalRowsFailed">Count of Failed rows.</param>
/// <param name="IsDryRun">True when the run was a DryRun.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MigrationRunSummaryDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string PlanSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string TriggerKind,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalSourceRowsSeen,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalRowsImported,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalRowsUpdated,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalRowsSkipped,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalRowsFailed,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsDryRun);

/// <summary>R2430 / TOR M4 — outbound projection of a migration batch.</summary>
/// <param name="Id">Sqid-encoded batch id.</param>
/// <param name="BatchOrdinal">1-based ordinal of the batch within its run.</param>
/// <param name="RowsInBatch">Total source rows in the batch.</param>
/// <param name="RowsImported">Count of Imported rows.</param>
/// <param name="RowsUpdated">Count of Updated rows.</param>
/// <param name="RowsSkipped">Count of Skipped rows.</param>
/// <param name="RowsFailed">Count of Failed rows.</param>
/// <param name="DurationMs">Wall-clock duration in milliseconds.</param>
/// <param name="ProcessedAt">UTC instant the batch was finalised.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MigrationBatchDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int BatchOrdinal,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowsInBatch,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowsImported,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowsUpdated,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowsSkipped,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowsFailed,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long DurationMs,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime ProcessedAt);

/// <summary>R2430 / R2433 / TOR M4 — outbound projection of a migration finding.</summary>
/// <param name="Id">Sqid-encoded finding id.</param>
/// <param name="RunSqid">Sqid-encoded parent run id.</param>
/// <param name="BatchOrdinal">1-based batch ordinal.</param>
/// <param name="RowOrdinalInBatch">0-based row ordinal within the batch.</param>
/// <param name="Severity">Stable enum-name of the severity.</param>
/// <param name="FindingCode">Stable dot-separated finding code.</param>
/// <param name="Description">PII-free human description.</param>
/// <param name="SourceFingerprint">Opaque source-row fingerprint.</param>
/// <param name="Acknowledged">True once an operator acknowledged the finding.</param>
/// <param name="AcknowledgedAt">UTC instant of acknowledgement; null while pending.</param>
/// <param name="AcknowledgedByUserSqid">Sqid of the acknowledging operator; null while pending.</param>
/// <param name="AcknowledgementNote">Operator note; null while pending.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MigrationFindingDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RunSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int BatchOrdinal,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowOrdinalInBatch,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Severity,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string FindingCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Description,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string SourceFingerprint,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool Acknowledged,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? AcknowledgedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? AcknowledgedByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? AcknowledgementNote);

/// <summary>R2430 / TOR M4 — filter envelope for the runs list endpoint.</summary>
/// <param name="Status">Optional status filter (stable enum-name).</param>
/// <param name="TriggerKind">Optional trigger-kind filter (stable enum-name).</param>
/// <param name="PlanSqid">Optional plan-id Sqid filter.</param>
/// <param name="Skip">Page offset (default 0; must be ≥ 0).</param>
/// <param name="Take">Page size (default 50; max 100).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record MigrationRunFilterDto(
    string? Status = null,
    string? TriggerKind = null,
    string? PlanSqid = null,
    int Skip = 0,
    int Take = 50);

/// <summary>R2430 / TOR M4 — paged envelope returned by the runs list endpoint.</summary>
/// <param name="Items">Runs on the requested page.</param>
/// <param name="Total">Total matching runs.</param>
/// <param name="Skip">Page offset that was applied.</param>
/// <param name="Take">Page size that was applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MigrationRunPageDto(
    IReadOnlyList<MigrationRunDto> Items,
    int Total,
    int Skip,
    int Take);

/// <summary>R2430 / TOR M4 — filter envelope for the run-details endpoint.</summary>
/// <param name="Skip">Findings page offset (default 0; must be ≥ 0).</param>
/// <param name="Take">Findings page size (default 50; max 200).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record MigrationRunDetailsFilterDto(
    int Skip = 0,
    int Take = 50);

/// <summary>R2430 / R2433 / TOR M4 — filter envelope for the findings list endpoint.</summary>
/// <param name="Severity">Optional severity filter (stable enum-name).</param>
/// <param name="RunSqid">Optional run-id Sqid filter.</param>
/// <param name="FindingCode">Optional finding-code filter.</param>
/// <param name="Acknowledged">Optional acknowledgement-state filter.</param>
/// <param name="Skip">Page offset (default 0; must be ≥ 0).</param>
/// <param name="Take">Page size (default 50; max 200).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record MigrationFindingFilterDto(
    string? Severity = null,
    string? RunSqid = null,
    string? FindingCode = null,
    bool? Acknowledged = null,
    int Skip = 0,
    int Take = 50);

/// <summary>R2430 / R2433 / TOR M4 — paged envelope returned by the findings list endpoint.</summary>
/// <param name="Items">Findings on the requested page.</param>
/// <param name="Total">Total matching findings.</param>
/// <param name="Skip">Page offset that was applied.</param>
/// <param name="Take">Page size that was applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MigrationFindingPageDto(
    IReadOnlyList<MigrationFindingDto> Items,
    int Total,
    int Skip,
    int Take);

/// <summary>R2433 / TOR M4 — input envelope for acknowledging a migration finding.</summary>
/// <param name="Note">Operator-supplied note (3..1000 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MigrationFindingAcknowledgeInputDto(string Note);

/// <summary>R2430 / R2431 / TOR M4 — run + paged findings envelope returned by the run-details endpoint.</summary>
/// <param name="Run">The run projection.</param>
/// <param name="Findings">Paged findings for the run.</param>
/// <param name="Batches">All batches recorded for the run, ordered by BatchOrdinal ASC.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MigrationRunDetailsDto(
    MigrationRunDto Run,
    MigrationFindingPageDto Findings,
    IReadOnlyList<MigrationBatchDto> Batches);

/// <summary>R2433 / TOR M4 — outbound projection of a reconciliation report.</summary>
/// <param name="Id">Sqid-encoded report id.</param>
/// <param name="RunSqid">Sqid-encoded parent run id.</param>
/// <param name="Status">Stable enum-name of the reconciliation outcome.</param>
/// <param name="SourceRowCount">Source-side row count.</param>
/// <param name="TargetRowCount">Target-side row count.</param>
/// <param name="MissingInTargetCount">Count of fingerprints missing from the staging table.</param>
/// <param name="UnexpectedInTargetCount">Count of fingerprints unexpectedly present in the staging table.</param>
/// <param name="ChecksumMatchRate">Match-rate decimal (0..1, 4 decimal places).</param>
/// <param name="DiscrepancyDetailsJson">Opaque JSON discrepancy listing (≤ 100 entries); may be null.</param>
/// <param name="ComputedAt">UTC instant the reconciliation was computed.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record ReconciliationReportDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RunSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long SourceRowCount,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TargetRowCount,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long MissingInTargetCount,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long UnexpectedInTargetCount,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    decimal ChecksumMatchRate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? DiscrepancyDetailsJson,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime ComputedAt);

/// <summary>R2431 / TOR M4 — admin-peek projection of a single migration staging row.</summary>
/// <param name="Id">Sqid-encoded staging-row id.</param>
/// <param name="RunSqid">Sqid-encoded parent run id.</param>
/// <param name="BatchOrdinal">1-based batch ordinal.</param>
/// <param name="RowOrdinalInBatch">0-based row ordinal within the batch.</param>
/// <param name="TargetEntityName">Symbolic destination aggregate.</param>
/// <param name="TargetEntityKey">Opaque natural key into the destination.</param>
/// <param name="MappedFieldsJson">JSON-encoded mapped fields (Confidential — may carry PII).</param>
/// <param name="SourceFingerprint">Opaque source-row fingerprint.</param>
/// <param name="IsCommitted">True once the row was committed.</param>
/// <param name="CommittedAt">UTC instant of commit; null while pending.</param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record MigrationStagingRowDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RunSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int BatchOrdinal,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowOrdinalInBatch,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string TargetEntityName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string TargetEntityKey,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string MappedFieldsJson,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string SourceFingerprint,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsCommitted,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? CommittedAt);
