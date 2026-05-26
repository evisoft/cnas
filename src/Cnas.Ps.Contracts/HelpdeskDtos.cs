using System;
using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R2500 / TOR PIR 020-023 — helpdesk DTOs: category registry, ticket lifecycle,
// comments, SLA events. All Id fields are Sqid-encoded per CLAUDE.md RULE 3.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>R2500 / TOR PIR 020-023 — outbound projection of a helpdesk category.</summary>
/// <param name="Id">Sqid-encoded category id.</param>
/// <param name="Code">Stable SCREAMING_SNAKE_CASE category code.</param>
/// <param name="DisplayName">Human-readable display name.</param>
/// <param name="Description">Optional free-form description.</param>
/// <param name="DefaultSeverity">Stable enum-name of the default severity applied to new tickets.</param>
/// <param name="FirstResponseSlaMinutes">Per-category first-response SLA target in minutes.</param>
/// <param name="ResolutionSlaMinutes">Per-category resolution SLA target in minutes.</param>
/// <param name="EscalationQueueCode">Symbolic queue routing identifier used at escalation.</param>
/// <param name="IsActive">True when the category is selectable for new tickets.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record SupportTicketCategoryDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Code,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DefaultSeverity,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int FirstResponseSlaMinutes,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int ResolutionSlaMinutes,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string EscalationQueueCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsActive);

/// <summary>R2500 / TOR PIR 020-023 — input envelope for creating a helpdesk category.</summary>
/// <param name="Code">Stable SCREAMING_SNAKE_CASE category code (≤ 64 chars).</param>
/// <param name="DisplayName">Display name (3..256 chars).</param>
/// <param name="Description">Optional free-form description (≤ 1000 chars).</param>
/// <param name="DefaultSeverity">Stable enum-name of the default severity.</param>
/// <param name="FirstResponseSlaMinutes">First-response SLA in minutes (5..7200).</param>
/// <param name="ResolutionSlaMinutes">Resolution SLA in minutes (30..43200).</param>
/// <param name="EscalationQueueCode">Symbolic escalation queue identifier (≤ 64 chars, SCREAMING_SNAKE_CASE).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record SupportTicketCategoryCreateInputDto(
    string Code,
    string DisplayName,
    string? Description,
    string DefaultSeverity,
    int FirstResponseSlaMinutes,
    int ResolutionSlaMinutes,
    string EscalationQueueCode);

/// <summary>R2500 / TOR PIR 020-023 — input envelope for modifying an existing helpdesk category.</summary>
/// <param name="DisplayName">Display name (3..256 chars).</param>
/// <param name="Description">Optional free-form description.</param>
/// <param name="DefaultSeverity">Stable enum-name of the default severity.</param>
/// <param name="FirstResponseSlaMinutes">First-response SLA in minutes (5..7200).</param>
/// <param name="ResolutionSlaMinutes">Resolution SLA in minutes (30..43200).</param>
/// <param name="EscalationQueueCode">Symbolic escalation queue identifier.</param>
/// <param name="ChangeReason">Free-form reason (3..1000 chars).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record SupportTicketCategoryModifyInputDto(
    string? DisplayName,
    string? Description,
    string? DefaultSeverity,
    int? FirstResponseSlaMinutes,
    int? ResolutionSlaMinutes,
    string? EscalationQueueCode,
    string ChangeReason);

/// <summary>R2500 / TOR PIR 020-023 — input envelope for reason-bearing transitions on the category surface.</summary>
/// <param name="Reason">Free-form reason (3..1000 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record SupportTicketCategoryReasonInputDto(string Reason);

/// <summary>R2500 / TOR PIR 020-023 — filter envelope for the category list endpoint.</summary>
/// <param name="IsActive">Optional IsActive filter.</param>
/// <param name="Skip">Page offset (default 0; must be ≥ 0).</param>
/// <param name="Take">Page size (default 50; max 100).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record SupportTicketCategoryFilterDto(
    bool? IsActive = null,
    int Skip = 0,
    int Take = 50);

/// <summary>R2500 / TOR PIR 020-023 — paged envelope returned by the category list endpoint.</summary>
/// <param name="Items">Categories on the requested page.</param>
/// <param name="Total">Total matching categories across all pages.</param>
/// <param name="Skip">Page offset that was applied.</param>
/// <param name="Take">Page size that was applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record SupportTicketCategoryPageDto(
    IReadOnlyList<SupportTicketCategoryDto> Items,
    int Total,
    int Skip,
    int Take);

/// <summary>R2500 / TOR PIR 020-023 — outbound projection of a single ticket comment.</summary>
/// <param name="Id">Sqid-encoded comment id.</param>
/// <param name="AuthorUserSqid">Sqid-encoded author user id.</param>
/// <param name="Body">Comment body — may contain user-supplied PII.</param>
/// <param name="IsInternalOnly">True when only operators should see the comment.</param>
/// <param name="PostedAt">UTC instant the comment was posted.</param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record SupportTicketCommentDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string AuthorUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string Body,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsInternalOnly,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime PostedAt);

/// <summary>R2500 / TOR PIR 020-023 — outbound projection of a single SLA event.</summary>
/// <param name="Id">Sqid-encoded SLA-event id.</param>
/// <param name="EventKind">Stable enum-name of the SLA event.</param>
/// <param name="DetectedAt">UTC instant the evaluator detected the event.</param>
/// <param name="Notes">Optional PII-free annotation.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record SupportTicketSlaEventDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string EventKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime DetectedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes);

/// <summary>R2500 / TOR PIR 020-023 — outbound projection of a helpdesk ticket (detail view).</summary>
/// <param name="Id">Sqid-encoded ticket id.</param>
/// <param name="TicketNumber">Deterministic TKT-{year}-{seq} ticket number.</param>
/// <param name="CategoryCode">Denormalised stable category code (avoids a catalog round-trip).</param>
/// <param name="Title">Short title; treated Internal at egress.</param>
/// <param name="Description">Free-form body — may contain user-supplied PII; treated Confidential.</param>
/// <param name="Severity">Stable enum-name of the current severity.</param>
/// <param name="Status">Stable enum-name of the lifecycle status.</param>
/// <param name="SubmittedByUserSqid">Sqid-encoded requester user id.</param>
/// <param name="AssignedToUserSqid">Sqid-encoded assignee user id (null while unassigned).</param>
/// <param name="SubmittedAt">UTC instant the ticket was submitted.</param>
/// <param name="FirstAcknowledgedAt">UTC instant of first acknowledge transition; null until then.</param>
/// <param name="ResolvedAt">UTC instant the ticket reached Resolved; null otherwise.</param>
/// <param name="ClosedAt">UTC instant the ticket reached Closed; null otherwise.</param>
/// <param name="FirstResponseDueAt">Computed first-response deadline.</param>
/// <param name="ResolutionDueAt">Computed resolution deadline.</param>
/// <param name="EscalatedAt">UTC instant the ticket was escalated; null otherwise.</param>
/// <param name="EscalationReason">Operator (or auto) supplied escalation reason.</param>
/// <param name="ResolutionSummary">Operator-supplied resolution summary; null until resolved.</param>
/// <param name="CancelReason">Operator-supplied cancellation reason; null unless cancelled.</param>
/// <param name="Comments">Chronologically-ordered comment timeline.</param>
/// <param name="SlaEvents">Chronologically-ordered SLA event timeline.</param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record SupportTicketDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string TicketNumber,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string CategoryCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Title,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Severity,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string SubmittedByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? AssignedToUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime SubmittedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? FirstAcknowledgedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? ResolvedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? ClosedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime FirstResponseDueAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime ResolutionDueAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? EscalatedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? EscalationReason,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string? ResolutionSummary,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CancelReason,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    IReadOnlyList<SupportTicketCommentDto> Comments,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<SupportTicketSlaEventDto> SlaEvents);

/// <summary>R2500 / TOR PIR 020-023 — input envelope for submitting a new helpdesk ticket.</summary>
/// <param name="CategoryCode">Stable category code (required).</param>
/// <param name="Title">Short title (3..256 chars).</param>
/// <param name="Description">Free-form body (3..8000 chars).</param>
/// <param name="Severity">Optional severity override (enum-name); when null the category default is used.</param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record SupportTicketSubmitInputDto(
    string CategoryCode,
    string Title,
    string Description,
    string? Severity = null);

/// <summary>R2500 / TOR PIR 020-023 — input envelope for the operator-assign endpoint.</summary>
/// <param name="AssignedToUserSqid">Sqid of the operator the ticket is being assigned to.</param>
/// <param name="Note">Free-form internal note recorded against the assignment (3..500 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record SupportTicketAssignInputDto(
    string AssignedToUserSqid,
    string Note);

/// <summary>R2500 / TOR PIR 020-023 — input envelope for reason-bearing transitions (escalate / cancel / request-reply / resume).</summary>
/// <param name="Reason">Free-form reason (3..500 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record SupportTicketReasonInputDto(string Reason);

/// <summary>R2500 / TOR PIR 020-023 — input envelope for the resolve endpoint.</summary>
/// <param name="Summary">Resolution summary (3..2000 chars).</param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record SupportTicketResolutionInputDto(string Summary);

/// <summary>R2500 / TOR PIR 020-023 — input envelope for the add-comment endpoint.</summary>
/// <param name="Body">Comment body (3..8000 chars).</param>
/// <param name="IsInternalOnly">True if only operators should see the comment.</param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record SupportTicketCommentInputDto(
    string Body,
    bool IsInternalOnly);

/// <summary>R2500 / TOR PIR 020-023 — filter envelope for the ticket list endpoint.</summary>
/// <param name="Status">Optional status filter (enum-name).</param>
/// <param name="CategoryCode">Optional category-code filter.</param>
/// <param name="SubmittedByUserSqid">Optional requester filter (Sqid).</param>
/// <param name="AssignedToUserSqid">Optional assignee filter (Sqid).</param>
/// <param name="Severity">Optional severity filter (enum-name).</param>
/// <param name="Skip">Page offset (default 0; must be ≥ 0).</param>
/// <param name="Take">Page size (default 50; max 100).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record SupportTicketFilterDto(
    string? Status = null,
    string? CategoryCode = null,
    string? SubmittedByUserSqid = null,
    string? AssignedToUserSqid = null,
    string? Severity = null,
    int Skip = 0,
    int Take = 50);

/// <summary>R2500 / TOR PIR 020-023 — paged envelope returned by the ticket list endpoint.</summary>
/// <param name="Items">Tickets on the requested page (without the comment + SLA event lists).</param>
/// <param name="Total">Total matching tickets across all pages.</param>
/// <param name="Skip">Page offset applied.</param>
/// <param name="Take">Page size applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record SupportTicketPageDto(
    IReadOnlyList<SupportTicketDto> Items,
    int Total,
    int Skip,
    int Take);
