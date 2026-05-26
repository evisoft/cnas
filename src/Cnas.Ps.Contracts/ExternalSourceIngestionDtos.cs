using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0203 / TOR CF 20.06 — DTOs for the per-source external-system ingestion
// registry. Every <c>Id</c> field is Sqid-encoded per CLAUDE.md RULE 3. No
// <see cref="…"/> references into Core (Contracts must stay Core-free).
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0203 — outbound projection of an <c>ExternalSourceIngestionRun</c>. Carries
/// the lifecycle status, the source code, the per-source counters, and the
/// sanitised failure reason if any.
/// </summary>
/// <param name="Id">Sqid-encoded run id.</param>
/// <param name="RunNumber">Human-friendly business-key (e.g. <c>ESI-2026-000001</c>).</param>
/// <param name="SourceCode">Upper-case source-system code (e.g. <c>RSP</c>, <c>RSUD</c>).</param>
/// <param name="Status">Stable enum-name of the current lifecycle status.</param>
/// <param name="TriggerKind">Stable enum-name of the run's trigger origin.</param>
/// <param name="StartedAtUtc">UTC instant the importer began this run.</param>
/// <param name="CompletedAtUtc">UTC instant the importer finalised this run. Null while in flight.</param>
/// <param name="TotalRecordsPulled">Records pulled from the upstream source.</param>
/// <param name="TotalRecordsApplied">Records successfully applied locally.</param>
/// <param name="TotalRecordsSkipped">Records skipped (idempotent / unchanged).</param>
/// <param name="TotalRecordsFailed">Records that failed during apply.</param>
/// <param name="FailureReason">Sanitised PII-free failure reason. Null on success.</param>
/// <param name="UpstreamPullId">Opaque upstream pull-id from the connector. Null when none.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record ExternalSourceIngestionRunDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string RunNumber,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string SourceCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string TriggerKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime StartedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? CompletedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalRecordsPulled,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalRecordsApplied,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalRecordsSkipped,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalRecordsFailed,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? FailureReason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? UpstreamPullId);

/// <summary>
/// R0203 — outcome envelope returned by an <c>IExternalSourceConnector</c>
/// fetch. Carries the per-source counters plus the opaque upstream pull-id so
/// the ingestion service can persist them onto the parent run row.
/// </summary>
/// <param name="RecordsPulled">Records pulled from the upstream source.</param>
/// <param name="RecordsApplied">Records successfully applied locally.</param>
/// <param name="RecordsSkipped">Records skipped (idempotent).</param>
/// <param name="RecordsFailed">Records that failed during apply.</param>
/// <param name="UpstreamPullId">Opaque upstream pull-id. Null when none.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record ExternalSourceFetchOutcomeDto(
    long RecordsPulled,
    long RecordsApplied,
    long RecordsSkipped,
    long RecordsFailed,
    string? UpstreamPullId);

/// <summary>
/// R0203 — filter envelope for the runs-list endpoint.
/// </summary>
/// <param name="SourceCode">Optional upper-case source code filter.</param>
/// <param name="Status">Optional status filter (stable enum-name string).</param>
/// <param name="TriggerKind">Optional trigger-kind filter (stable enum-name string).</param>
/// <param name="Skip">Page offset (default 0; must be ≥ 0).</param>
/// <param name="Take">Page size (default 50; max 100).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record ExternalSourceIngestionRunFilterDto(
    string? SourceCode = null,
    string? Status = null,
    string? TriggerKind = null,
    int Skip = 0,
    int Take = 50);

/// <summary>
/// R0203 — paged envelope returned by the runs-list endpoint.
/// </summary>
/// <param name="Items">Runs on the requested page.</param>
/// <param name="Total">Total matching runs across all pages.</param>
/// <param name="Skip">Page offset that was applied.</param>
/// <param name="Take">Page size that was applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record ExternalSourceIngestionRunPageDto(
    IReadOnlyList<ExternalSourceIngestionRunDto> Items,
    int Total,
    int Skip,
    int Take);

/// <summary>
/// R0203 — input envelope for the manual ingestion-trigger admin endpoint.
/// </summary>
/// <param name="SourceCode">Upper-case source-system code. Required.</param>
/// <param name="AsOfDate">
/// Optional as-of date the connector should target. When null the service
/// substitutes today (UTC).
/// </param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record ExternalSourceManualTriggerInputDto(
    string SourceCode,
    DateOnly? AsOfDate = null);
