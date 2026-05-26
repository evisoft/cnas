using Cnas.Ps.Core.Audit;

namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2273 / TOR SEC 027 — generic 4-eyes substrate row. One row per pending sensitive
/// administrative change request that needs a SECOND distinct operator to approve before
/// the underlying action is allowed to take effect.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a generic substrate?</b> The older <see cref="PendingAdminAction"/> aggregate
/// (R0058) models one concrete maker-checker queue with its own executor interface. SEC
/// 027 calls for the 4-eyes principle to apply to a growing list of sensitive admin
/// surfaces (role grants, account-state changes, executory-doc cancels, legal-change
/// "mark ready", and more). Rather than fan that out one queue per surface, this
/// substrate carries the workflow once and exposes two extension seams: a per-action
/// <c>ISensitiveActionPolicy</c> (validates the payload schema + tunes expiration) and a
/// per-action <c>ISensitiveActionHandler</c> (executes the mutation on approval). New
/// sensitive actions become a single policy + handler registration; the queue, the
/// audit, the expiry sweeper, and the metrics are shared.
/// </para>
/// <para>
/// <b>Workflow.</b> The requester opens a row through
/// <c>ISensitiveAdminActionService.RequestAsync</c>; the service stamps
/// <see cref="RequestedByUserId"/>, <see cref="RequestedAt"/>, sets
/// <see cref="Status"/> = <see cref="SensitiveAdminActionStatus.PendingApproval"/>, and
/// computes <see cref="ExpiresAt"/>. A SECOND distinct operator later calls
/// <c>ApproveAsync</c> or <c>RejectAsync</c>. The service enforces the
/// <c>ApproverUserId != RequestedByUserId</c> invariant at the boundary. On approve,
/// the service invokes the registered handler and writes the handler's outcome into
/// <see cref="ExecutionResultJson"/> / <see cref="ExecutionFailureReason"/>.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the surrogate id
/// crosses the boundary on the admin REST surface as a Sqid (CLAUDE.md RULE 3 / ARH
/// 027).
/// </para>
/// <para>
/// <b>PII discipline.</b> <see cref="RequestPayloadJson"/> /
/// <see cref="ExecutionResultJson"/> are treated <c>Confidential</c> at the contracts
/// layer. The substrate never logs the raw payload; the handler is responsible for
/// keeping PII out of the serialised result. <see cref="ExecutionFailureReason"/> is
/// sanitised by the service before persistence — class name + short stable message, no
/// stack traces.
/// </para>
/// </remarks>
[AutoAudit(Severity = AuditSeverity.Critical, EventCodePrefix = "SENSITIVE_ADMIN_ACTION")]
public sealed class SensitiveAdminAction : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable, SCREAMING_SNAKE_CASE identifier of the kind of action being proposed
    /// (e.g. <c>USER.ROLE_GRANT</c>, <c>USER.ACCOUNT_STATE_CHANGE</c>,
    /// <c>EXECUTORY_DOC.CANCEL</c>, <c>LEGAL_CHANGE.MARK_READY</c>). Validated at the
    /// service boundary with the regex <c>^[A-Z][A-Z0-9_.]{1,63}$</c>. Determines which
    /// <c>ISensitiveActionPolicy</c> validates the payload and which
    /// <c>ISensitiveActionHandler</c> executes the mutation on approval.
    /// </summary>
    public required string ActionCode { get; set; }

    /// <summary>Current lifecycle state — see <see cref="SensitiveAdminActionStatus"/>.</summary>
    public SensitiveAdminActionStatus Status { get; set; } = SensitiveAdminActionStatus.PendingApproval;

    /// <summary>Raw <c>UserProfile.Id</c> of the operator that opened the request.</summary>
    public long RequestedByUserId { get; set; }

    /// <summary>UTC instant at which the request was opened.</summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>
    /// Free-form rationale captured from the requester (3..1000 chars). Mandatory so the
    /// audit trail always carries a human-readable "why".
    /// </summary>
    public required string RequestReason { get; set; }

    /// <summary>
    /// Opaque JSON payload describing the input parameters of the proposed action.
    /// Shape is owned by the matching <c>ISensitiveActionPolicy</c> / handler. Capped at
    /// 8192 bytes at the validator + DB layer. Treated <c>Confidential</c>; never logged
    /// raw.
    /// </summary>
    public required string RequestPayloadJson { get; set; }

    /// <summary>Raw <c>UserProfile.Id</c> of the second operator that approved the request, or null while pending.</summary>
    public long? ApprovedByUserId { get; set; }

    /// <summary>UTC instant of approval, or null while pending.</summary>
    public DateTime? ApprovedAt { get; set; }

    /// <summary>
    /// Mandatory approver-supplied note captured at approval time (3..1000 chars).
    /// Preserved on the audit trail.
    /// </summary>
    public string? ApprovalNote { get; set; }

    /// <summary>Raw <c>UserProfile.Id</c> of the operator that rejected the request, or null otherwise.</summary>
    public long? RejectedByUserId { get; set; }

    /// <summary>UTC instant of rejection, or null otherwise.</summary>
    public DateTime? RejectedAt { get; set; }

    /// <summary>
    /// Operator-supplied rejection reason (3..1000 chars) populated when
    /// <see cref="Status"/> = <see cref="SensitiveAdminActionStatus.Rejected"/>.
    /// </summary>
    public string? RejectionReason { get; set; }

    /// <summary>UTC instant the original requester (or admin) cancelled the request.</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>Operator-supplied cancellation reason (3..1000 chars) when cancelled.</summary>
    public string? CancelReason { get; set; }

    /// <summary>
    /// UTC instant after which the request auto-expires if still pending. Computed at
    /// create time — default <see cref="RequestedAt"/> + 72 h, optionally overridden by
    /// the per-action policy's <c>ExpirationOverride</c>.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// UTC instant the registered handler executed (or was determined to be missing).
    /// Stamped alongside the transition to
    /// <see cref="SensitiveAdminActionStatus.Executed"/> or
    /// <see cref="SensitiveAdminActionStatus.ExecutionFailed"/>.
    /// </summary>
    public DateTime? ExecutedAt { get; set; }

    /// <summary>
    /// Handler-emitted JSON result (must be PII-free). Captured on
    /// <see cref="SensitiveAdminActionStatus.Executed"/> for downstream observability.
    /// </summary>
    public string? ExecutionResultJson { get; set; }

    /// <summary>
    /// Sanitised failure reason captured on
    /// <see cref="SensitiveAdminActionStatus.ExecutionFailed"/>. Capped at 1000 chars,
    /// never contains a stack trace, never contains PII.
    /// </summary>
    public string? ExecutionFailureReason { get; set; }
}
