using System;
using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R2506 / TOR PIR 037-040 — DTOs for the QA-risk registry. All Id fields are
// Sqid-encoded per CLAUDE.md RULE 3. Codes and statuses are Public;
// descriptions / closure / completion notes are Internal because they may
// reference internal processes and systems.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>R2506 / TOR PIR 037-040 — outbound projection of a quality risk.</summary>
/// <param name="Id">Sqid-encoded risk id.</param>
/// <param name="RiskCode">Stable SCREAMING_SNAKE_CASE risk code.</param>
/// <param name="Title">Short title.</param>
/// <param name="Description">Free-form description.</param>
/// <param name="Category">Stable enum-name of the risk category.</param>
/// <param name="Likelihood">Stable enum-name of the likelihood band.</param>
/// <param name="Impact">Stable enum-name of the impact band.</param>
/// <param name="Status">Stable enum-name of the lifecycle state.</param>
/// <param name="OwnerSqid">Sqid of the owner user.</param>
/// <param name="IdentifiedAt">UTC instant the risk was identified.</param>
/// <param name="LastReviewedAt">UTC instant of the last recorded review (or null).</param>
/// <param name="ClosedAt">UTC instant the risk was closed (or null).</param>
/// <param name="ClosureReason">Free-form closure reason (or null).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record QualityRiskDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string RiskCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Title,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Category,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Likelihood,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Impact,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string OwnerSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime IdentifiedAt,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime? LastReviewedAt,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime? ClosedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ClosureReason);

/// <summary>R2506 / TOR PIR 037-040 — input envelope for creating a quality risk.</summary>
/// <param name="RiskCode">Stable SCREAMING_SNAKE_CASE code.</param>
/// <param name="Title">Short title (3..256 chars).</param>
/// <param name="Description">Free-form description (50..4000 chars).</param>
/// <param name="Category">Stable enum-name of the category.</param>
/// <param name="Likelihood">Stable enum-name of the likelihood band.</param>
/// <param name="Impact">Stable enum-name of the impact band.</param>
/// <param name="OwnerSqid">Sqid of the owner user (mandatory).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record QualityRiskCreateInputDto(
    string RiskCode,
    string Title,
    string Description,
    string Category,
    string Likelihood,
    string Impact,
    string OwnerSqid);

/// <summary>R2506 / TOR PIR 037-040 — input envelope for modifying an existing quality risk.</summary>
/// <param name="Title">Optional new title.</param>
/// <param name="Description">Optional new description.</param>
/// <param name="Category">Optional new category.</param>
/// <param name="Likelihood">Optional new likelihood.</param>
/// <param name="Impact">Optional new impact.</param>
/// <param name="OwnerSqid">Optional new owner.</param>
/// <param name="ChangeReason">Free-form reason for the change (3..1000 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record QualityRiskModifyInputDto(
    string? Title,
    string? Description,
    string? Category,
    string? Likelihood,
    string? Impact,
    string? OwnerSqid,
    string ChangeReason);

/// <summary>R2506 / TOR PIR 037-040 — input envelope recording a periodic review.</summary>
/// <param name="ReviewNote">Free-form review note (3..1000 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record QualityRiskReviewInputDto(string ReviewNote);

/// <summary>R2506 / TOR PIR 037-040 — reason-bearing input envelope (close / accept).</summary>
/// <param name="Reason">Free-form reason (3..1000 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record QualityRiskReasonInputDto(string Reason);

/// <summary>R2506 / TOR PIR 037-040 — filter envelope for the quality-risk list endpoint.</summary>
/// <param name="Status">Optional status filter (stable enum-name).</param>
/// <param name="Category">Optional category filter (stable enum-name).</param>
/// <param name="Likelihood">Optional likelihood filter (stable enum-name).</param>
/// <param name="Impact">Optional impact filter (stable enum-name).</param>
/// <param name="OwnerSqid">Optional owner filter (Sqid).</param>
/// <param name="OverdueForReview">Optional — when true, return only risks not reviewed in &gt; 365 days.</param>
/// <param name="Skip">Page offset (default 0; ≥ 0).</param>
/// <param name="Take">Page size (default 50; 1..100).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record QualityRiskFilterDto(
    string? Status = null,
    string? Category = null,
    string? Likelihood = null,
    string? Impact = null,
    string? OwnerSqid = null,
    bool? OverdueForReview = null,
    int Skip = 0,
    int Take = 50);

/// <summary>R2506 / TOR PIR 037-040 — paged envelope returned by the quality-risk list endpoint.</summary>
/// <param name="Items">Risks on the requested page.</param>
/// <param name="Total">Total matching rows.</param>
/// <param name="Skip">Page offset applied.</param>
/// <param name="Take">Page size applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record QualityRiskPageDto(
    IReadOnlyList<QualityRiskDto> Items,
    int Total,
    int Skip,
    int Take);

/// <summary>R2506 / TOR PIR 037-040 — outbound projection of a preventive action.</summary>
/// <param name="Id">Sqid-encoded action id.</param>
/// <param name="RiskSqid">Sqid of the parent risk.</param>
/// <param name="Description">Description of the planned mitigation.</param>
/// <param name="Status">Stable enum-name of the action state.</param>
/// <param name="DueDate">Calendar due date.</param>
/// <param name="AssignedToSqid">Sqid of the assignee.</param>
/// <param name="CompletedAt">UTC instant marked Implemented (or null).</param>
/// <param name="CompletionNote">Free-form completion note (or null).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record QualityRiskActionDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RiskSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateOnly DueDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string AssignedToSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime? CompletedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CompletionNote);

/// <summary>R2506 / TOR PIR 037-040 — input envelope for adding a preventive action.</summary>
/// <param name="Description">Description of the planned mitigation (3..2000 chars).</param>
/// <param name="DueDate">Calendar due date.</param>
/// <param name="AssignedToSqid">Sqid of the assignee (mandatory).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record QualityRiskActionCreateInputDto(
    string Description,
    DateOnly DueDate,
    string AssignedToSqid);

/// <summary>R2506 / TOR PIR 037-040 — input envelope for modifying a preventive action.</summary>
/// <param name="Description">Optional new description.</param>
/// <param name="DueDate">Optional new due date.</param>
/// <param name="AssignedToSqid">Optional new assignee.</param>
/// <param name="ChangeReason">Free-form reason for the change (3..1000 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record QualityRiskActionModifyInputDto(
    string? Description,
    DateOnly? DueDate,
    string? AssignedToSqid,
    string ChangeReason);

/// <summary>R2506 / TOR PIR 037-040 — input envelope for marking an action Implemented.</summary>
/// <param name="CompletionNote">Free-form completion note (3..1000 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record QualityRiskActionImplementInputDto(string CompletionNote);

/// <summary>R2506 / TOR PIR 037-040 — reason-bearing input envelope (cancel-action).</summary>
/// <param name="Reason">Free-form reason (3..500 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record QualityRiskActionReasonInputDto(string Reason);
