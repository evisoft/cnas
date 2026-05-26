using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R2161 / TOR INT 002 — DTOs for the generic CnasUser-facing offline-batch
// (ingest + export) endpoints. Separate from the R1710 Annex-4 B2B surface
// (OfflineBatchSubmissionDto) — those carry op-coded request CSVs from a
// signed-in B2B consumer; these are the simpler, user-facing fallback.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R2161 / TOR INT 002 — input envelope for <c>POST /api/offline-batch/ingest</c>.
/// The service rejects payloads exceeding 10 000 rows with
/// <c>VALIDATION_FAILED</c>.
/// </summary>
/// <param name="Description">
/// Optional human-readable description of the batch contents (1..256 chars).
/// Surfaces on admin tooling so an operator can correlate a row to the
/// caller's intent without opening the payload.
/// </param>
/// <param name="Rows">
/// Multi-record payload. Each row is a free-form string the processor
/// interprets per the deployed schema. The count is validated server-side
/// against the 10 000-row cap.
/// </param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record OfflineBatchIngestInputDto(
    string? Description,
    IReadOnlyList<string> Rows);

/// <summary>
/// R2161 / TOR INT 002 — input envelope for <c>POST /api/offline-batch/export</c>.
/// The service rejects payloads exceeding 10 000 rows with
/// <c>VALIDATION_FAILED</c>.
/// </summary>
/// <param name="Description">
/// Optional human-readable description of the export request (1..256 chars).
/// Surfaces on admin tooling so an operator can correlate a row to the
/// caller's intent without opening the payload.
/// </param>
/// <param name="Filters">
/// Multi-record filter payload. Each row is a free-form string the processor
/// interprets per the deployed export schema. The count is validated
/// server-side against the 10 000-row cap so an export request cannot ship
/// an unbounded predicate list.
/// </param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record OfflineBatchExportInputDto(
    string? Description,
    IReadOnlyList<string> Filters);

/// <summary>
/// R2161 / TOR INT 002 — outbound projection of an
/// <see cref="Cnas.Ps.Contracts.OfflineBatchJobDto"/>. The <see cref="Id"/>
/// field is a Sqid; raw long ids never leave the system.
/// </summary>
/// <param name="Id">Sqid-encoded job id.</param>
/// <param name="Kind">Stable enum-name — <c>Ingest</c> or <c>Export</c>.</param>
/// <param name="Status">Stable enum-name of the current lifecycle state.</param>
/// <param name="SubmittedAtUtc">UTC submission timestamp.</param>
/// <param name="StartedAtUtc">UTC timestamp the processor began running this job.</param>
/// <param name="CompletedAtUtc">UTC timestamp the processor finalised this job.</param>
/// <param name="ErrorMessage">Sanitised processor failure reason — populated only when <c>Status</c> is <c>Failed</c>.</param>
/// <param name="ResultBlobKey">Opaque blob-storage key for the produced artefact — populated only when <c>Status</c> is <c>Completed</c>.</param>
/// <param name="RowCount">Count of records the consumer submitted.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record OfflineBatchJobDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Kind,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime SubmittedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? StartedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? CompletedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ErrorMessage,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ResultBlobKey,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RowCount);
