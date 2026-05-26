namespace Cnas.Ps.Core.Domain;

/// <summary>
/// A sensitive administrative action that is pending a second-administrator approval
/// before it takes effect (R0058 — SEC 027 "Maker-checker / 4-eyes mode").
/// </summary>
/// <remarks>
/// <para>
/// <b>Workflow.</b> The first administrator (<i>maker</i>) submits a request through
/// <c>IPendingAdminActionService.SubmitAsync</c>; that call serialises the action's
/// payload to <see cref="PayloadJson"/>, stamps <see cref="MakerUserId"/> +
/// <see cref="MakerRequestedAtUtc"/>, sets <see cref="Status"/> = <see cref="PendingAdminActionStatus.Pending"/>,
/// and computes <see cref="ExpiresAtUtc"/> = <see cref="MakerRequestedAtUtc"/> + TTL
/// (default 24 h). A second administrator (<i>checker</i>) — who MUST be a different
/// user — later calls <c>ApproveAsync</c> or <c>RejectAsync</c>; the service performs
/// the maker ≠ checker guard, the TTL guard, and the already-decided guard before
/// invoking the executor and flipping the status.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the action's id is
/// surfaced on the admin list via <c>PendingAdminActionItem.Id</c> as a Sqid-encoded
/// string (CLAUDE.md RULE 3 / ARH 027). The raw <see cref="AuditableEntity.Id"/>
/// never leaves the system.
/// </para>
/// <para>
/// <b>PII discipline.</b> <see cref="PayloadJson"/> stores only the routing payload the
/// executor needs to apply the action — never the citizen's IDNP, phone, email, or
/// bank IBAN. The worked-example executor (currently a no-op demo) is responsible for
/// validating its own payload shape; future per-action executors MUST keep PII out of
/// the payload so the pending-actions store does not become a back-door PII sink.
/// </para>
/// <para>
/// <b>Soft-delete + audit.</b> Inherits the standard auditing fields
/// (<see cref="AuditableEntity.CreatedAtUtc"/>, <see cref="AuditableEntity.UpdatedAtUtc"/>,
/// <see cref="AuditableEntity.IsActive"/>, <see cref="AuditableEntity.Xmin"/>) from
/// <see cref="AuditableEntity"/>. The expiry sweeper flips <see cref="Status"/> on
/// stale rows; rows are never hard-deleted because their history is part of the
/// SEC 042 trasabilitate trail.
/// </para>
/// </remarks>
public sealed class PendingAdminAction : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable, screaming-snake-case operation code identifying which executor handles
    /// the action when it is approved (e.g. <c>USER.SUSPEND</c>, <c>ROLE.REVOKE</c>,
    /// <c>DEMO.NOOP</c>). Validated at submit time by checking the registered
    /// <c>IPendingAdminActionExecutor</c> set; unknown codes are rejected fail-fast
    /// with <see cref="Cnas.Ps.Core.Common.ErrorCodes.MakerCheckerUnknownOperation"/>.
    /// </summary>
    public required string Operation { get; set; }

    /// <summary>
    /// Serialised payload describing what the executor must do when the action is
    /// approved. Opaque to the maker-checker workflow itself — only the matching
    /// <c>IPendingAdminActionExecutor</c> understands the shape.
    /// </summary>
    /// <remarks>
    /// Stored as PostgreSQL <c>text</c> (no length cap at the database layer) because
    /// future actions may carry small structured forms. MUST NOT embed PII; see the
    /// class-level remarks for the rationale.
    /// </remarks>
    public required string PayloadJson { get; set; }

    /// <summary>Raw <c>UserProfile.Id</c> of the administrator that submitted the action (the maker).</summary>
    public long MakerUserId { get; set; }

    /// <summary>UTC instant at which the maker submitted the action.</summary>
    public DateTime MakerRequestedAtUtc { get; set; }

    /// <summary>
    /// Raw <c>UserProfile.Id</c> of the administrator that approved or rejected the
    /// action (the checker). <c>null</c> while the row is still
    /// <see cref="PendingAdminActionStatus.Pending"/> or
    /// <see cref="PendingAdminActionStatus.Expired"/> (auto-expiry does not record a
    /// checker — no human decided).
    /// </summary>
    public long? CheckerUserId { get; set; }

    /// <summary>
    /// UTC instant at which the checker decided. <c>null</c> while the row is still
    /// <see cref="PendingAdminActionStatus.Pending"/> or auto-expired without a human
    /// decision.
    /// </summary>
    public DateTime? CheckerDecidedAtUtc { get; set; }

    /// <summary>Current lifecycle state — see <see cref="PendingAdminActionStatus"/>.</summary>
    public required PendingAdminActionStatus Status { get; set; }

    /// <summary>
    /// Free-form rejection reason captured from the checker when
    /// <see cref="Status"/> = <see cref="PendingAdminActionStatus.Rejected"/>;
    /// <c>null</c> for any other status. Capped at 512 characters by the EF
    /// configuration so a runaway message cannot fill a row.
    /// </summary>
    public string? RejectionReason { get; set; }

    /// <summary>
    /// UTC instant after which the action auto-expires. Computed at submit time
    /// (<see cref="MakerRequestedAtUtc"/> + TTL, default 24 h). The service-side
    /// approve guard rejects late approvals; a background sweeper additionally flips
    /// <see cref="Status"/> on stale rows so the admin list stays clean even without
    /// approval traffic.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }
}
