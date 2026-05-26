using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.SensitiveActions;

/// <summary>
/// R2273 / TOR SEC 027 — service façade for the generic 4-eyes admin workflow. Owns the
/// request → approve / reject / cancel → execute lifecycle, the expiry sweeper hook,
/// and the audit + metric emission. Concrete sensitive actions plug in via
/// <see cref="ISensitiveActionPolicy"/> + <see cref="ISensitiveActionHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Same-operator invariant.</b> The service rejects any approve / reject where the
/// approver id equals the requester id with
/// <see cref="Cnas.Ps.Core.Common.ErrorCodes.Conflict"/> +
/// <c>FOUR_EYES.SAME_OPERATOR</c>. This is the very point of the 4-eyes ceremony.
/// </para>
/// <para>
/// <b>Audit attribution.</b> Every successful mutation emits a stable audit event at
/// <see cref="Cnas.Ps.Core.Domain.AuditSeverity.Critical"/> severity (CLAUDE.md §5.6 —
/// sensitive admin operation). Failures emit no audit row to avoid log-pumping on
/// repeated invalid calls; the controller surfaces the failure to the caller via the
/// <see cref="Result{T}"/> envelope.
/// </para>
/// <para>
/// <b>Sqid round-trip.</b> Every identifier crossing the boundary is Sqid-encoded per
/// CLAUDE.md RULE 3 — the service decodes them internally before touching the
/// DbContext.
/// </para>
/// </remarks>
public interface ISensitiveAdminActionService
{
    /// <summary>Opens a new 4-eyes request. The current operator (from caller context) is the requester.</summary>
    /// <param name="input">Validated request envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The persisted DTO on success; structured failure otherwise.</returns>
    Task<Result<SensitiveAdminActionDto>> RequestAsync(
        SensitiveAdminActionRequestInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// Approves a pending request as the second distinct operator. On approval, invokes
    /// the registered <see cref="ISensitiveActionHandler"/> and records its outcome.
    /// </summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="input">Approval envelope (mandatory note).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<SensitiveAdminActionDto>> ApproveAsync(
        string sqid,
        SensitiveAdminActionApprovalInputDto input,
        CancellationToken ct = default);

    /// <summary>Rejects a pending request as the second distinct operator.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<SensitiveAdminActionDto>> RejectAsync(
        string sqid,
        SensitiveAdminActionReasonInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// Cancels a pending request. Typically invoked by the original requester (or an
    /// admin) before any approver has decided. Terminal-state requests cannot be
    /// cancelled.
    /// </summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<SensitiveAdminActionDto>> CancelAsync(
        string sqid,
        SensitiveAdminActionReasonInputDto input,
        CancellationToken ct = default);

    /// <summary>Fetches a single request by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The DTO when found; <see cref="Cnas.Ps.Core.Common.ErrorCodes.NotFound"/> otherwise.</returns>
    Task<Result<SensitiveAdminActionDto>> GetByIdAsync(string sqid, CancellationToken ct = default);

    /// <summary>Lists requests according to the supplied filter envelope.</summary>
    /// <param name="filter">Optional filter envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>A paged DTO; never null.</returns>
    Task<Result<SensitiveAdminActionPageDto>> ListAsync(
        SensitiveAdminActionFilterDto filter,
        CancellationToken ct = default);

    /// <summary>
    /// Bulk-flips <c>PendingApproval</c> rows whose <c>ExpiresAt</c> has elapsed to
    /// <c>Expired</c>. Invoked by the background expiry-sweep job; safe to call inline
    /// from ops endpoints.
    /// </summary>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The number of rows flipped on this call.</returns>
    Task<Result<int>> SweepExpiredAsync(CancellationToken ct = default);
}
