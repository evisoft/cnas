using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R2273 / TOR SEC 027 — generic 4-eyes admin workflow DTOs
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R2273 / TOR SEC 027 — projection of one sensitive-admin-action request as it leaves
/// the system. The substrate carries the workflow once; per-action shape lives inside
/// <see cref="RequestPayloadJson"/> and <see cref="ExecutionResultJson"/>.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying request row.</param>
/// <param name="ActionCode">Stable SCREAMING_SNAKE_CASE identifier of the action kind.</param>
/// <param name="Status">Stable enum-name representation of the lifecycle state.</param>
/// <param name="RequestedByUserSqid">Sqid-encoded id of the operator that opened the request.</param>
/// <param name="RequestedAt">UTC instant the request was opened.</param>
/// <param name="RequestReason">Operator-supplied rationale (3..1000 chars).</param>
/// <param name="RequestPayloadJson">Opaque JSON payload of the proposed action's input parameters. Confidential.</param>
/// <param name="ApprovedByUserSqid">Sqid-encoded id of the approver, when applicable.</param>
/// <param name="ApprovedAt">UTC instant of approval, when applicable.</param>
/// <param name="ApprovalNote">Approver-supplied note (3..1000 chars), when applicable.</param>
/// <param name="RejectedByUserSqid">Sqid-encoded id of the rejecter, when applicable.</param>
/// <param name="RejectedAt">UTC instant of rejection, when applicable.</param>
/// <param name="RejectionReason">Operator-supplied rejection reason (3..1000 chars), when applicable.</param>
/// <param name="CancelledAt">UTC instant of cancellation, when applicable.</param>
/// <param name="CancelReason">Operator-supplied cancellation reason (3..1000 chars), when applicable.</param>
/// <param name="ExpiresAt">UTC instant after which the request auto-expires if still pending.</param>
/// <param name="ExecutedAt">UTC instant the handler executed (or was determined missing), when applicable.</param>
/// <param name="ExecutionResultJson">Handler-emitted JSON result (PII-free), when applicable.</param>
/// <param name="ExecutionFailureReason">Sanitised failure reason on <c>ExecutionFailed</c>, when applicable.</param>
public sealed record SensitiveAdminActionDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ActionCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string RequestedByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime RequestedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RequestReason,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string RequestPayloadJson,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? ApprovedByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime? ApprovedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ApprovalNote,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? RejectedByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime? RejectedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? RejectionReason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime? CancelledAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CancelReason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime ExpiresAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime? ExecutedAt,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string? ExecutionResultJson,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string? ExecutionFailureReason);

/// <summary>
/// R2273 / TOR SEC 027 — request envelope opened by the first operator.
/// </summary>
/// <param name="ActionCode">Stable SCREAMING_SNAKE_CASE identifier. Required; max 64 chars.</param>
/// <param name="RequestReason">Operator-supplied rationale. Required; 3..1000 chars.</param>
/// <param name="RequestPayloadJson">Opaque JSON payload. Required; size 1..8192 bytes when serialised.</param>
public sealed record SensitiveAdminActionRequestInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ActionCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RequestReason,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string RequestPayloadJson);

/// <summary>
/// R2273 / TOR SEC 027 — approval envelope posted by the second operator. The mandatory
/// note ensures every approval carries a human-readable justification on the audit trail.
/// </summary>
/// <param name="Note">Approver-supplied justification. Required; 3..1000 chars.</param>
public sealed record SensitiveAdminActionApprovalInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Note);

/// <summary>
/// R2273 / TOR SEC 027 — reason envelope used by reject + cancel endpoints.
/// </summary>
/// <param name="Reason">Operator-supplied reason. Required; 3..1000 chars.</param>
public sealed record SensitiveAdminActionReasonInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);

/// <summary>
/// R2273 / TOR SEC 027 — filter envelope for the list endpoint.
/// </summary>
/// <param name="Status">Optional stable enum-name filter — null returns all statuses.</param>
/// <param name="ActionCode">Optional action-code filter — null returns all codes.</param>
/// <param name="RequestedAfter">Optional UTC lower bound on <c>RequestedAt</c>.</param>
/// <param name="RequestedBefore">Optional UTC upper bound on <c>RequestedAt</c>.</param>
/// <param name="Skip">Page offset; ≥ 0.</param>
/// <param name="Take">Page size; 1..100.</param>
public sealed record SensitiveAdminActionFilterDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Status = null,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? ActionCode = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime? RequestedAfter = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime? RequestedBefore = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip = 0,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take = 25);

/// <summary>
/// R2273 / TOR SEC 027 — paged response envelope for the list endpoint.
/// </summary>
/// <param name="Items">Sensitive-admin-action page.</param>
/// <param name="Total">Total matching rows across all pages.</param>
/// <param name="Skip">Echoed page offset.</param>
/// <param name="Take">Echoed page size.</param>
public sealed record SensitiveAdminActionPageDto(
    IReadOnlyList<SensitiveAdminActionDto> Items,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Total,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take);

/// <summary>
/// R2273 / TOR SEC 027 — descriptor row returned by the registry endpoint. One row per
/// registered <c>ISensitiveActionPolicy</c>, enumerating the well-known action codes
/// that operators can submit.
/// </summary>
/// <param name="ActionCode">Stable SCREAMING_SNAKE_CASE identifier.</param>
/// <param name="DisplayLabel">Short human-readable label suitable for picker UI.</param>
/// <param name="ExpirationHours">Per-action expiration override in hours; null indicates the substrate default (72h).</param>
public sealed record SensitiveActionRegistryEntryDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ActionCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DisplayLabel,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    double? ExpirationHours);
