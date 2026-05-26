namespace Cnas.Ps.Contracts;

/// <summary>
/// R0362 / UC13 — input DTO for submitting a workflow-driven profile-update request.
/// </summary>
/// <param name="TargetContributorSqid">Sqid id of the <c>InsuredPerson</c> whose profile is being updated.</param>
/// <param name="Type">Stable string matching <c>ProfileUpdateRequestType</c> — e.g. <c>"Address"</c>, <c>"Contact"</c>.</param>
/// <param name="RequestedChangesJson">JSON payload matching the corresponding contributor input DTO.</param>
/// <param name="Note">Optional free-text note shown to the approver alongside the request.</param>
public sealed record ProfileUpdateRequestSubmitDto(
    string TargetContributorSqid,
    string Type,
    string RequestedChangesJson,
    string? Note);

/// <summary>
/// R0362 / UC13 — output DTO for a <c>ProfileUpdateRequest</c> row (defined in Cnas.Ps.Core.Domain).
/// </summary>
/// <param name="Id">Sqid-encoded id of the profile-update request row.</param>
/// <param name="ServiceApplicationSqid">Sqid-encoded id of the parent <c>ServiceApplication</c>.</param>
/// <param name="TargetContributorSqid">Sqid-encoded id of the target <c>InsuredPerson</c>.</param>
/// <param name="Type">Stable string value of <c>ProfileUpdateRequestType</c>.</param>
/// <param name="Status">Stable string value of <c>ProfileUpdateRequestStatus</c>.</param>
/// <param name="RequestedChangesJson">Verbatim JSON payload as submitted.</param>
/// <param name="RejectionReason">Rejection rationale when <paramref name="Status"/> is <c>Rejected</c>; null otherwise.</param>
/// <param name="AppliedAtUtc">UTC instant of successful apply; null until <paramref name="Status"/> is <c>Applied</c>.</param>
/// <param name="ApprovedByUserSqid">Sqid id of the administrator who approved; null while pending/rejected.</param>
/// <param name="ApplicationErrorJson">Failure envelope captured when apply failed; null otherwise.</param>
public sealed record ProfileUpdateRequestDto(
    string Id,
    string ServiceApplicationSqid,
    string TargetContributorSqid,
    string Type,
    string Status,
    string RequestedChangesJson,
    string? RejectionReason,
    DateTime? AppliedAtUtc,
    string? ApprovedByUserSqid,
    string? ApplicationErrorJson);

/// <summary>R0362 — request body for the reject endpoint.</summary>
/// <param name="Reason">Free-text rejection rationale (1..1024 chars).</param>
public sealed record ProfileUpdateRejectRequest(string Reason);

/// <summary>
/// R0363 / UC13 — output DTO for a <c>ProfileRefreshRun</c> row (defined in Cnas.Ps.Core.Domain).
/// </summary>
/// <param name="Id">Sqid-encoded id of the refresh-run row.</param>
/// <param name="Source">Stable source code (<c>RSP</c> / <c>RSUD</c> / <c>SI_SFS</c>).</param>
/// <param name="TargetContributorSqid">Sqid-encoded id of the refreshed contributor; null for batch runs.</param>
/// <param name="Outcome">Stable string value of <c>ProfileRefreshOutcome</c>.</param>
/// <param name="RowsApplied">Number of deltas applied successfully.</param>
/// <param name="RowsSkipped">Number of deltas skipped or failed.</param>
/// <param name="StartedUtc">UTC instant the run started.</param>
/// <param name="CompletedUtc">UTC instant the run completed; null while still running.</param>
/// <param name="FailureSummary">Truncated failure summary on partial-failure / failed runs.</param>
public sealed record ProfileRefreshRunDto(
    string Id,
    string Source,
    string? TargetContributorSqid,
    string Outcome,
    int RowsApplied,
    int RowsSkipped,
    DateTime StartedUtc,
    DateTime? CompletedUtc,
    string? FailureSummary);

/// <summary>
/// R0363 — one delta returned by an external-data gateway. Stateless wire shape; the
/// gateway is responsible for canonicalising the upstream payload into the field-level
/// granularity the writer expects.
/// </summary>
/// <param name="ChildEntityType">
/// Stable string identifying which contributor child table the delta targets:
/// <c>Address</c>, <c>Contact</c>, <c>CivilStatus</c>, <c>Activity</c>,
/// <c>SocialInsuranceContract</c>. Matches <c>ProfileUpdateRequestType</c>.
/// </param>
/// <param name="FieldName">Free-text label for the field that changed (audit/log use).</param>
/// <param name="OldValue">Previous value as seen by the gateway (null when first-time observation).</param>
/// <param name="NewValue">New value from the upstream payload.</param>
/// <param name="PayloadJson">
/// JSON payload matching the corresponding contributor <c>*InputDto</c> shape that the
/// service deserialises and forwards to the contributor-side writer.
/// </param>
public sealed record ProfileRefreshDeltaDto(
    string ChildEntityType,
    string FieldName,
    string? OldValue,
    string? NewValue,
    string PayloadJson);
