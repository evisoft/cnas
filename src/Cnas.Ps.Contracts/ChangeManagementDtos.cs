using System;
using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R2505 / TOR PIR 030-033 — DTOs for the change-management aggregate.
// All Id fields are Sqid-encoded per CLAUDE.md RULE 3. Free-text fields that
// may reference systems / processes (Description, RollbackPlan, signature
// references) are classified Internal; codes and statuses are Public.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>R2505 / TOR PIR 030-033 — outbound projection of a change request.</summary>
/// <param name="Id">Sqid-encoded change-request id.</param>
/// <param name="ChangeNumber">Deterministic CHG-{year}-{seq} number.</param>
/// <param name="Title">Short title.</param>
/// <param name="Description">Free-form description (may reference systems).</param>
/// <param name="Kind">Stable enum-name (Standard / Normal / Emergency).</param>
/// <param name="Status">Stable enum-name of lifecycle state.</param>
/// <param name="Risk">Stable enum-name of declared risk (Low / Medium / High).</param>
/// <param name="ImpactedSystems">Free-text list of impacted systems.</param>
/// <param name="RollbackPlan">Mandatory rollback plan (50..4000 chars).</param>
/// <param name="TestEnvironmentValidationNote">Note recorded at TestEnvValidated transition (or null).</param>
/// <param name="TestValidatedBySqid">Sqid of the test-env validator (or null).</param>
/// <param name="TestValidatedAt">UTC instant of test-env validation (or null).</param>
/// <param name="CodeSignatureReference">Opaque reference to the signed artefact (or null).</param>
/// <param name="CodeSignedBySqid">Sqid of the code-signer (or null).</param>
/// <param name="CodeSignedAt">UTC instant the code signature was recorded (or null).</param>
/// <param name="RequestedBySqid">Sqid of the requester (always populated).</param>
/// <param name="ApprovedBySqid">Sqid of the production approver (or null).</param>
/// <param name="ApprovedAt">UTC instant the change was approved for production (or null).</param>
/// <param name="DeployedAt">UTC instant the production deployment completed (or null).</param>
/// <param name="RolledBackAt">UTC instant the change was rolled back (or null).</param>
/// <param name="RollbackReason">Free-form rollback reason (or null).</param>
/// <param name="CancelReason">Free-form cancellation reason (or null).</param>
/// <param name="RelatedMaintenanceWindowSqid">Optional Sqid of an associated maintenance window.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record ChangeRequestDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ChangeNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Title,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Kind,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Risk,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ImpactedSystems,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RollbackPlan,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? TestEnvironmentValidationNote,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? TestValidatedBySqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime? TestValidatedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CodeSignatureReference,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CodeSignedBySqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime? CodeSignedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RequestedBySqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ApprovedBySqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime? ApprovedAt,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime? DeployedAt,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime? RolledBackAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? RollbackReason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CancelReason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? RelatedMaintenanceWindowSqid);

/// <summary>R2505 / TOR PIR 030-033 — input envelope for creating a change request.</summary>
/// <param name="Title">Short title (3..256 chars).</param>
/// <param name="Description">Free-form description (50..8000 chars).</param>
/// <param name="Kind">Stable enum-name (Standard / Normal / Emergency).</param>
/// <param name="Risk">Stable enum-name of declared risk (Low / Medium / High).</param>
/// <param name="ImpactedSystems">Free-text list of impacted systems (3..1000 chars).</param>
/// <param name="RollbackPlan">Mandatory rollback plan (50..4000 chars).</param>
/// <param name="RelatedMaintenanceWindowSqid">Optional Sqid of an associated maintenance window.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record ChangeRequestCreateInputDto(
    string Title,
    string Description,
    string Kind,
    string Risk,
    string ImpactedSystems,
    string RollbackPlan,
    string? RelatedMaintenanceWindowSqid);

/// <summary>R2505 / TOR PIR 030-033 — input envelope for the test-env validation transition.</summary>
/// <param name="ValidationNote">Free-form note recorded with the validation (3..2000 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record ChangeRequestTestValidationInputDto(string ValidationNote);

/// <summary>R2505 / TOR PIR 030-033 — input envelope for the code-signed transition.</summary>
/// <param name="CodeSignatureReference">Opaque reference (3..128 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record ChangeRequestSignCodeInputDto(string CodeSignatureReference);

/// <summary>R2505 / TOR PIR 030-033 — input envelope for the rollback transition.</summary>
/// <param name="Reason">Free-form rollback reason (3..2000 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record ChangeRequestRollbackInputDto(string Reason);

/// <summary>R2505 / TOR PIR 030-033 — reason-bearing input envelope (cancel).</summary>
/// <param name="Reason">Free-form reason (3..500 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record ChangeRequestReasonInputDto(string Reason);

/// <summary>R2505 / TOR PIR 030-033 — filter envelope for the change-request list endpoint.</summary>
/// <param name="Status">Optional status filter (stable enum-name).</param>
/// <param name="Kind">Optional kind filter (stable enum-name).</param>
/// <param name="Risk">Optional risk filter (stable enum-name).</param>
/// <param name="Skip">Page offset (default 0; ≥ 0).</param>
/// <param name="Take">Page size (default 50; 1..100).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record ChangeRequestFilterDto(
    string? Status = null,
    string? Kind = null,
    string? Risk = null,
    int Skip = 0,
    int Take = 50);

/// <summary>R2505 / TOR PIR 030-033 — paged envelope returned by the change-request list endpoint.</summary>
/// <param name="Items">Change requests on the requested page.</param>
/// <param name="Total">Total matching rows.</param>
/// <param name="Skip">Page offset applied.</param>
/// <param name="Take">Page size applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record ChangeRequestPageDto(
    IReadOnlyList<ChangeRequestDto> Items,
    int Total,
    int Skip,
    int Take);
